/* Value editors only build conditions. Matching and blank/category decisions belong to the backend. */
(function (global) {
  'use strict';
  var Ui = global.JetUi;
  // 值清單上限一律用 Ui.TYPED_SET_MAX_VALUES：fieldValue 的清單與文字包含比對，
  // 後端都以 FilterScenarioLimits.MaxTypedInValuesPerRule 驗證（架構測試守住本檔不再寫死上限）。
  // 標籤鏡像 Domain FieldValueConditions.Labels（FilterAstFrontendContractTests 逐鍵守衛）。
  var labels = { equals: '等於', notEquals: '不等於', contains: '包含任一文字', notContains: '不包含任何文字',
    startsWith: '開頭符合', notStartsWith: '開頭不符合', endsWith: '結尾符合', notEndsWith: '結尾不符合', in: '符合清單任一值', notIn: '不在清單中',
    on: '指定日期', before: '早於', onOrBefore: '當日或以前', after: '晚於', onOrAfter: '當日或以後',
    dayOfMonthIn: '每月幾日屬於', dayOfMonthNotIn: '每月幾日不屬於',
    monthStartDays: '每月月初天數', notMonthStartDays: '排除每月月初天數', monthEndDays: '每月月底天數', notMonthEndDays: '排除每月月底天數',
    isWeekend: '週末', isNotWeekend: '週末以外', isHoliday: '假日清單中的日期', isNotHoliday: '假日清單以外的日期',
    isMakeupDay: '補班日', isNotMakeupDay: '補班日以外', isNonBusinessDay: '非營業日（排除補班日）', isNotNonBusinessDay: '非營業日以外（含補班日）',
    endsWithDigits: '整數尾數符合', notEndsWithDigits: '整數尾數不符合',
    greaterThan: '大於', greaterThanOrEqual: '大於或等於', lessThan: '小於', lessThanOrEqual: '小於或等於',
    between: '介於', notBetween: '不介於', isBlank: '空白', isNotBlank: '非空白' };
  var operators = {
    text: ['contains', 'notContains', 'equals', 'notEquals', 'startsWith', 'notStartsWith', 'endsWith', 'notEndsWith', 'in', 'notIn', 'isBlank', 'isNotBlank'],
    date: ['in', 'notIn', 'dayOfMonthIn', 'dayOfMonthNotIn', 'on', 'notEquals', 'before', 'onOrBefore', 'after', 'onOrAfter', 'between', 'notBetween',
      'monthStartDays', 'notMonthStartDays', 'monthEndDays', 'notMonthEndDays',
      'isWeekend', 'isNotWeekend', 'isHoliday', 'isNotHoliday', 'isMakeupDay', 'isNotMakeupDay', 'isNonBusinessDay', 'isNotNonBusinessDay', 'isBlank', 'isNotBlank'],
    money: ['equals', 'notEquals', 'greaterThan', 'greaterThanOrEqual', 'lessThan', 'lessThanOrEqual', 'between', 'notBetween', 'in', 'notIn', 'endsWithDigits', 'notEndsWithDigits', 'isBlank', 'isNotBlank']
  };
  function isDayOfMonth(op) { return op === 'dayOfMonthIn' || op === 'dayOfMonthNotIn'; }
  function isMonthWindow(op) { return /^(notM|m)onth(Start|End)Days$/.test(op); }
  function carrier(op, type) {
    if (/^(isBlank|isNotBlank)$/.test(op) || isCalendar(op)) { return 'none'; }
    if (/^(notBetween|between)$/.test(op)) { return 'range'; }
    if (/^(notIn|in)$/.test(op) || isDayOfMonth(op)) { return 'set'; }
    if (type === 'text' && /^(contains|notContains)$/.test(op)) { return 'set'; }
    return 'value';
  }
  function isCalendar(op) { return /^is(Not)?(Weekend|Holiday|MakeupDay|NonBusinessDay)$/.test(op); }
  function isTail(op) { return op === 'endsWithDigits' || op === 'notEndsWithDigits'; }
  function fields(state) {
    return Ui.FILTER_DATE_FIELDS.map(function (key) { return { id: key, label: Ui.glFieldLabel(key), type: 'date' }; })
      .concat(Ui.FILTER_TEXT_FIELDS.map(function (key) { return { id: key, label: Ui.glFieldLabel(key), type: 'text' }; }))
      .concat([{ id: 'amount', label: '金額', type: 'money' }])
      .concat(Ui.committedRdeFields(state).map(function (field) { return { id: field.fieldId, label: field.label, type: field.valueType, extra: true }; }));
  }
  function field(rule, state) { return fields(state).find(function (item) { return item.id === (rule.fieldId || rule.field); }); }
  function fieldType(rule, state) { var selected = field(rule, state); return selected ? selected.type : (rule.__valueType || 'date'); }
  // 2026-09-04 裁定：欄位空白的分錄預設不列入，勾選「空白也符合」才列入；與後端 GlRulePredicates.FieldValue 一致。
  function blankMatches(rule) { return rule.includeBlank === true; }
  function day(year, month, date) { var value = new Date(0); value.setUTCHours(0, 0, 0, 0); value.setUTCFullYear(year, month - 1, date); return value; }
  function iso(value) { return String(value.getUTCFullYear()).padStart(4, '0') + '-' + String(value.getUTCMonth() + 1).padStart(2, '0') + '-' + String(value.getUTCDate()).padStart(2, '0'); }
  function splitDelimited(raw) {
    return String(raw == null ? '' : raw).split(/[,，、\t\r\n]+/).map(function (value) { return value.trim(); }).filter(Boolean);
  }
  function delimitedList(rule, state) {
    return isDayOfMonth(rule.operator) || fieldType(rule, state) === 'text' &&
      (/^(contains|notContains)$/.test(rule.operator) || !rule.fieldId && ['createBy', 'approveBy', 'accNum'].indexOf(rule.field) >= 0);
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
    var serial = moneyNumber(text);
    if (serial !== null && serial >= 1 && serial <= 2958465) {
      return iso(new Date(day(1899, 12, 30).getTime() + Math.floor(serial * 86400000 + 0.5)));
    }
    return text;
  }
  function values(rule, state) {
    var selected = field(rule, state);
    return Array.from(new Set((rule.values || []).map(function (value) {
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
      if (isTail(rule.operator)) { return splitDelimited(value).join(','); }
      return selected && selected.type === 'date' && !isDayOfMonth(rule.operator) && !isMonthWindow(rule.operator) ? normalizeDate(value || '', state) : String(value == null ? '' : value);
    }
    if (mode === 'value') { result.value = operand(rule.value); }
    if (mode === 'range') { result.from = operand(rule.from); result.to = operand(rule.to); }
    if (mode === 'set') { result.values = values(rule, state); }
    if (selected && selected.type === 'money') { result.amountBasis = rule.amountBasis || (selected.extra ? 'signed' : 'absolute'); }
    return result;
  }
  function summary(rule, state, compact) {
    var selected = field(rule, state);
    var value = wire(rule, state);
    var mode = carrier(rule.operator, fieldType(rule, state));
    var fieldLabel = selected ? selected.label : rule.fieldId || rule.field || '尚未選擇欄位';
    if (selected && selected.type === 'money' && mode !== 'none' && !isTail(rule.operator)) { fieldLabel += value.amountBasis === 'absolute' ? '絕對值' : '含正負號'; }
    var side = rule.drCr === 'debit' ? '借方分錄：' : rule.drCr === 'credit' ? '貸方分錄：' : '';
    var text = side + fieldLabel + ' ' + (labels[rule.operator] || '尚未選擇比較方式');
    if (mode === 'value') { text += '「' + value.value + '」'; }
    if (isMonthWindow(rule.operator)) { text += ' 天'; }
    if (mode === 'range') { text += '「' + value.from + '」至「' + value.to + '」'; }
    if (mode === 'set') { text += '「' + value.values.join('、') + '」'; }
    if ((mode !== 'none' || isCalendar(rule.operator)) && (!compact || blankMatches(rule))) { text += blankMatches(rule) ? '；空白也符合' : '；空白不列入'; }
    return text;
  }
  // 欄位下拉：同型別欄位、其他型別欄位與「分錄性質」（借貸別、人工／自動）都列出來，審計員在同一個下拉
  // 換欄位就換條件；換到其他型別由本模組重設運算子，換到分錄性質交回呼叫端換成對應的規則型別。
  function groupedFields(state) {
    var all = fields(state);
    function group(label, predicate) { return { label: label, fields: all.filter(predicate) }; }
    var special = ['createBy', 'approveBy', 'accNum', 'accName'];
    return [group('日期', function (item) { return item.type === 'date'; }),
      group('金額', function (item) { return item.type === 'money'; }),
      group('人員', function (item) { return ['createBy', 'approveBy'].indexOf(item.id) >= 0; }),
      group('會計科目', function (item) { return ['accNum', 'accName'].indexOf(item.id) >= 0; }),
      group('其他文字', function (item) { return item.type === 'text' && special.indexOf(item.id) < 0; })]
      .filter(function (item) { return item.fields.length > 0; });
  }
  function fieldOptionsHtml(state, selectedId, pseudoFields) {
    var groups = groupedFields(state).map(function (group) {
      return '<optgroup label="' + group.label + '">' + group.fields.map(function (item) {
        return '<option value="' + Ui.esc(item.id) + '"' + (item.id === selectedId ? ' selected' : '') + '>' + Ui.esc(item.label) + (item.extra ? '（攸關資料元素欄位）' : '') + '</option>';
      }).join('') + '</optgroup>';
    }).join('');
    var pseudo = (pseudoFields || []).length
      ? '<optgroup label="分錄性質">' + pseudoFields.map(function (item) {
          return '<option value="' + Ui.esc(item.id) + '"' + (item.id === selectedId ? ' selected' : '') + '>' + Ui.esc(item.label) + '</option>';
        }).join('') + '</optgroup>'
      : '';
    var available = fields(state).concat(pseudoFields || []).some(function (item) { return item.id === selectedId; });
    var unavailable = selectedId && !available ? '<option value="' + Ui.esc(selectedId) + '" selected disabled>此欄位目前未配對，請重新選擇</option>' : '';
    return unavailable + groups + pseudo;
  }
  // Presentation groups share an input intent; the original operator remains authoritative.
  var editorViews = new WeakMap();
  function editorView(rule) {
    if (!editorViews.has(rule)) { editorViews.set(rule, { optionsOpen: false, periodEnd: false, days: 7 }); }
    return editorViews.get(rule);
  }
  function hasAccountTree(rule) {
    return rule.field === 'accNum' && !rule.fieldId && (rule.operator === 'in' || rule.operator === 'notIn');
  }
  function accountTreeView(rule, state) {
    var view = editorView(rule), tree = view.accountTree;
    if (!tree || tree.project !== state.project || tree.generation !== state.dataGeneration || tree.taxonomy !== state.taxonomy) {
      tree = { project: state.project, generation: state.dataGeneration, taxonomy: state.taxonomy,
        open: false, categoryId: null, search: '', rows: [], cursor: null, nextCursor: null, sequence: 0,
        pending: false, error: '', loaded: false, lastRequest: null };
      view.accountTree = tree;
    }
    return tree;
  }
  function accountTreeHtml(rule, state, focusKey) {
    var tree = accountTreeView(rule, state), selected = new Set(values(rule, state));
    var categories = Ui.taxonomyTree(state);
    var categoryRows = categories.map(function (category) {
      var checked = tree.categoryId === category.categoryId && tree.loaded && !tree.cursor && !tree.nextCursor && tree.rows.length &&
        tree.rows.every(function (row) { return selected.has(row.accountCode); });
      return '<div class="account-tree__category" style="margin-inline-start:' + (category.depth * 16) + 'px">' +
        '<button type="button" class="btn btn--ghost btn--tiny" data-account-tree-category="' + Ui.esc(category.categoryId) +
        '" aria-expanded="' + (tree.categoryId === category.categoryId) + '">' + Ui.esc(category.label) + '</button>' +
        '<label><input type="checkbox" data-account-tree-select-category="' + Ui.esc(category.categoryId) + '"' +
        (checked ? ' checked' : '') + '>勾選此分類及下層科目</label></div>';
    }).join('');
    var groups = [];
    tree.rows.forEach(function (row) {
      var group = groups.find(function (item) { return item.categoryId === row.categoryId; });
      if (!group) { group = { categoryId: row.categoryId, rows: [] }; groups.push(group); }
      group.rows.push(row);
    });
    var accounts = groups.map(function (group) {
      var label = group.categoryId ? Ui.taxonomyCategoryLabel(state, group.categoryId) : '尚未分類';
      return '<section class="account-tree__accounts" aria-label="' + Ui.esc(label) + '的科目"><strong>' + Ui.esc(label) + '</strong>' +
        group.rows.map(function (row) {
          return '<div><label><input type="checkbox" data-account-tree-code="' + Ui.esc(row.accountCode) + '"' +
            (selected.has(row.accountCode) ? ' checked' : '') + '>' + Ui.esc(row.accountCode) +
            (row.accountName ? '　' + Ui.esc(row.accountName) : '') + '</label></div>';
        }).join('') + '</section>';
    }).join('');
    var status = tree.pending ? '正在載入科目…' : tree.loaded ?
      (tree.rows.length ? '本頁 ' + tree.rows.length + ' 個科目；已選 ' + selected.size + ' 個。' : '目前範圍沒有符合的科目。') : '先選分類，或搜尋本案科目。';
    return '<details class="value-editor__options" data-account-tree' + (tree.open ? ' open' : '') + '><summary>從科目樹選取</summary>' +
      '<div class="value-editor__aux"><label>搜尋科目編號或名稱<input type="search" data-account-tree-search' +
      ' data-focus-key="' + Ui.esc(focusKey + '-account-search') + '" aria-label="搜尋科目編號或名稱" maxlength="400" value="' + Ui.esc(tree.search) + '"></label>' +
      '<p class="rule-field__hint">分類會包含所有下層；勾選後寫入上方科目編號清單，不改變保留或排除方式。</p>' +
      '<button type="button" class="btn btn--ghost btn--tiny" data-account-tree-category="">查看全部科目</button>' + categoryRows +
      '<p data-account-tree-status role="status">' + status + '</p>' + accounts +
      (tree.nextCursor ? '<button type="button" class="btn btn--ghost btn--tiny" data-account-tree-more' + (tree.pending ? ' disabled' : '') + '>載入下一頁科目</button>' : '') +
      '<p data-account-tree-error role="alert"' + (tree.error ? '' : ' hidden') + '>' + Ui.esc(tree.error) + '</p>' +
      (tree.error && tree.lastRequest ? '<button type="button" class="btn btn--ghost btn--tiny" data-account-tree-retry>重新載入科目</button>' : '') +
      '</div></details>';
  }
  function bindAccountTree(root, rule, state, changed) {
    if (!hasAccountTree(rule)) { return; }
    var details = root.querySelector('[data-account-tree]');
    if (!details) { return; }
    var tree = accountTreeView(rule, state);
    function currentState() { return global.JetStore && global.JetStore.getState ? global.JetStore.getState() : state; }
    function containsRule(current) {
      function includes(rules) { return (rules || []).some(function (item) { return item === rule || includes(item.rules); }); }
      return !!(current.filter && current.filter.draft && (current.filter.draft.groups || []).some(function (group) { return includes(group.rules); }));
    }
    function active() {
      var current = currentState();
      return hasAccountTree(rule) && editorView(rule).accountTree === tree && current.project === tree.project &&
        current.dataGeneration === tree.generation && current.taxonomy === tree.taxonomy && containsRule(current);
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
            var codes = tree.rows.map(function (row) { return row.accountCode; });
            setCodes(codes, selection);
          }
        }
        redraw();
      }).catch(function (error) {
        if (!active() || tree.sequence !== sequence) { return; }
        tree.pending = false; tree.error = (error && error.message ? error.message : '科目清單讀取失敗。') + ' 可按「重新載入科目」重試。';
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
      control.addEventListener('change', function () { readPage(payload(control.getAttribute('data-account-tree-select-category'), null), control.checked); });
    });
    root.querySelectorAll('[data-account-tree-code]').forEach(function (control) {
      control.addEventListener('change', function () {
        if (!active()) { return; }
        tree.lastRequest = null;
        setCodes([control.getAttribute('data-account-tree-code')], control.checked); redraw();
      });
    });
    var search = root.querySelector('[data-account-tree-search]');
    if (search) search.addEventListener('input', function () {
      tree.search = search.value; readPage(payload(tree.categoryId, null), null);
    });
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
      { key: 'weekend', label: '週末（依案件設定）', yes: 'isWeekend', no: 'isNotWeekend' },
      { key: 'holiday', label: '假日清單', yes: 'isHoliday', no: 'isNotHoliday' },
      { key: 'makeup', label: '補班日清單', yes: 'isMakeupDay', no: 'isNotMakeupDay' },
      { key: 'nonBusiness', label: '非營業日（排除補班日）', yes: 'isNonBusinessDay', no: 'isNotNonBusinessDay' },
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
    money: [
      { key: 'exact', label: '指定金額', yes: 'in', no: 'notIn', aliases: ['equals', 'notEquals'] },
      { key: 'range', label: '金額區間', yes: 'between', no: 'notBetween' },
      { key: 'tails', label: '整數部分的特定尾數', yes: 'endsWithDigits', no: 'notEndsWithDigits' },
      { key: 'above', label: '高於金額', yes: 'greaterThan', aliases: ['greaterThanOrEqual'] },
      { key: 'below', label: '低於金額', yes: 'lessThan', aliases: ['lessThanOrEqual'] },
      { key: 'blank', label: '空白金額', yes: 'isBlank', no: 'isNotBlank' }
    ]
  };
  function selectedMode(rule, state) {
    return commonModes[fieldType(rule, state)].find(function (item) {
      return item.yes === rule.operator || item.no === rule.operator || (item.aliases || []).indexOf(rule.operator) >= 0;
    });
  }
  function excluded(rule, mode) { return !!mode && (rule.operator === mode.no || rule.operator === 'notEquals'); }
  function changeOperator(rule, state, operator) {
    if (carrier(operator, fieldType(rule, state)) !== carrier(rule.operator, fieldType(rule, state))) { rule.values = []; }
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
      return '這個比較方式只收一個值；目前輸入多行，仍會整段當成一個值，不會拆成多個條件。';
    }
    if (selected && selected.type === 'money' && !isTail(rule.operator) && value.amountBasis === 'absolute') {
      var inputs = mode === 'set' ? value.values : mode === 'range' ? [value.from, value.to] : [value.value];
      if (inputs.some(function (input) { var number = moneyNumber(input); return number !== null && number < 0; })) {
        return '目前比較金額絕對值，但條件填了負數。請確認這是預期門檻；若要保留正負號，可改用「含正負號」。這項提醒不會阻止計算。';
      }
    }
    return '';
  }
  function render(rule, state, focusKey, pseudoFields) {
    var selected = field(rule, state), type = fieldType(rule, state), mode = carrier(rule.operator, type);
    var choice = selectedMode(rule, state), view = editorView(rule);
    var direction = '<label>分錄方向<select data-value-key="drCr" aria-label="此條件的分錄方向"><option value=""' + (!rule.drCr ? ' selected' : '') + '>不限借貸</option>' +
      '<option value="debit"' + (rule.drCr === 'debit' ? ' selected' : '') + '>借方</option><option value="credit"' + (rule.drCr === 'credit' ? ' selected' : '') + '>貸方</option></select></label>';
    var showDirection = (!rule.fieldId && ['accNum', 'accName'].indexOf(rule.field) >= 0) || !!rule.drCr;
    var main = showDirection ? direction : '';
    if (choice) {
      main += choice.no ? '<select data-value-polarity aria-label="保留或排除"><option value="keep"' + (!excluded(rule, choice) ? ' selected' : '') +
        '>保留</option><option value="exclude"' + (excluded(rule, choice) ? ' selected' : '') + '>排除</option></select>' : '<span>保留</span>';
    } else { main += '<span class="value-advanced-current">' + Ui.esc(labels[rule.operator] || '請設定比較方式') + '</span>'; }
    main += '<select data-value-kind aria-label="篩選方式">' + (!choice ? '<option value="advanced" selected>其他比較方式</option>' : '') +
      commonModes[type].map(function (item) { return '<option value="' + item.key + '"' + (choice === item && !view.periodEnd ? ' selected' : '') + '>' + item.label + '</option>'; }).join('') +
      (type === 'date' && state.project && state.project.periodEnd ? '<option value="periodEnd"' + (view.periodEnd ? ' selected' : '') + '>查核期末最後幾天</option>' : '') + '</select>';
    function input(key, label) {
      // A textarea retains pasted line breaks so single-value comparisons can explain them instead of silently flattening them.
      if (type === 'text' || isTail(rule.operator)) { return '<textarea rows="1" class="value-editor__list" data-value-key="' + key + '" aria-label="' + label + '" placeholder="' + label + '">' + Ui.esc(rule[key] || '') + '</textarea>'; }
      return '<input type="text" data-value-key="' + key + '" aria-label="' + label + '" placeholder="' + label + '" value="' + Ui.esc(rule[key] || '') + '">';
    }
    var extra = [];
    var canIncludeBlank = (mode !== 'none' || isCalendar(rule.operator)) && !(rule.field === 'postDate' && !rule.fieldId);
    if (isCalendar(rule.operator)) {
      main += '<p class="rule-explanation">週末依案件的每週非工作日設定；假日與補班日依本案匯入的清單。非營業日為週末或假日，再排除補班日。清單空白時，不會自行推算國定假日。</p>';
    }
    if (isTail(rule.operator)) { main += '<p class="rule-explanation">忽略正負號及小數。' + delimiterHint + '例如 -1,001.45 符合 001，1 元不符合 001。</p>'; }
    if (!selected) { main += '<p class="form-notice">此欄位目前未配對，請回第三步確認，或移除後重新加入。</p>'; }
    if (isMonthWindow(rule.operator)) {
      main += '<input type="number" data-value-key="value" min="1" max="31" aria-label="每月天數（1 到 31）" value="' + Ui.esc(rule.value || '') + '"><span>天（含當天）</span>';
    } else if (mode === 'value') { main += input('value', isTail(rule.operator) ? '尾數（以逗號分隔）' : type === 'money' ? '金額' : '條件值'); }
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
      var isPerson = ['createBy', 'approveBy'].indexOf(rule.field) >= 0 && !rule.fieldId;
      var listHint = type === 'date' ? '每行一個日期' : type === 'text' && /^(contains|notContains)$/.test(rule.operator) ? '每行一個關鍵字' :
        isPerson ? '每行一個人員代號或姓名' : rule.field === 'accNum' ? '每行一個科目編號' : '每行一個值';
      var list = '<textarea rows="1" class="value-editor__list" data-value-key="values" aria-label="' + listHint + '" placeholder="' + listHint + '">' + Ui.esc((rule.values || []).join('\n')) + '</textarea>';
      if (delimitedList(rule, state)) { list += '<p class="rule-field__hint">' + delimiterHint + '</p>'; }
      else if (type === 'text') { list += '<p class="rule-field__hint">文字值清單每行一個值；值內的逗號、頓號與 Tab 保留，不會拆開。</p>'; }
      if (isPerson) { list += '<p class="form-notice">填入 GL 實際提供的姓名或員工代碼，重複項目會合併。姓名本身含逗號時也會拆開，請改填來源中的員工代碼。可在上方選保留或排除。</p>'; }
      if (type === 'date') {
        main += '<button type="button" class="btn btn--ghost btn--tiny" data-calendar-open data-focus-key="' + focusKey + '">選日期</button>' +
          '<div class="selected-dates" data-selected-dates aria-label="已選日期"></div>';
        extra.push('<label>貼上日期清單' + list + '</label>');
      } else { main += list; }
      extra.push('<p class="rule-field__hint">最多 ' + Ui.TYPED_SET_MAX_VALUES + ' 個不同值，空行不列入。' + (type === 'date' ? '日期可用 2025-08-01、2025/8/1、2025.8.1、20250801 或 Excel 序列值；民國年依案件設定。' : '保留文字前置零；金額千分位逗號不拆開。') + '</p>');
      if (current.length) { extra.push('<button type="button" class="btn btn--ghost btn--tiny" data-values-clear>清空所有值</button>'); }
    }
    if (type === 'money' && !isTail(rule.operator)) {
      var basis = rule.amountBasis || (selected && selected.extra ? 'signed' : 'absolute');
      extra.push('<label>金額比較基準<select data-value-key="amountBasis" aria-label="金額比較基準"><option value="absolute"' + (basis !== 'signed' ? ' selected' : '') + '>絕對值（不分借貸）</option><option value="signed"' + (basis === 'signed' ? ' selected' : '') + '>含正負號</option></select></label>');
    }
    if (type === 'date' && (mode === 'value' || mode === 'range') && !isMonthWindow(rule.operator)) {
      main += '<p class="rule-field__hint">日期寫法和 GL 相同，例如 yyyy/M/d、yyyy.M.d、yyyyMMdd 或 Excel 序列值；民國年依案件設定。</p>';
    }
    if (type === 'text' && mode === 'value') { main += '<p class="rule-field__hint">此比較方式只收一個值；不會將逗號或多行內容拆成多個條件。</p>'; }
    main += '<p class="form-notice" data-value-warning role="status"' + (warning(rule, state) ? '' : ' hidden') + '>' + Ui.esc(warning(rule, state)) + '</p>';
    if (canIncludeBlank) {
      extra.push('<label class="value-blank"><input type="checkbox" data-value-key="includeBlank"' + (blankMatches(rule) ? ' checked' : '') + '>' +
        (type === 'date' ? '沒有日期的分錄也列入結果' : '此欄位空白的分錄也列入結果') + '</label>');
    }
    if (hasAccountTree(rule)) { main += accountTreeHtml(rule, state, focusKey); }
    else if (rule.field === 'accNum' && !rule.fieldId) {
      main += '<p class="rule-field__hint">要從科目樹選取，請將篩選方式改為「完整內容相同」。</p>';
    }
    var activeNotes = [];
    if (canIncludeBlank && blankMatches(rule)) { activeNotes.push('含空白'); }
    if (type === 'money' && rule.amountBasis === 'signed' && !isTail(rule.operator)) { activeNotes.push('含正負號'); }
    return '<div class="value-editor" data-focus-prefix="' + focusKey + '"><div class="value-editor__main">' + main + '</div>' +
      (extra.length ? '<details class="value-editor__options" data-value-options' + (view.optionsOpen ? ' open' : '') + '><summary>' + (type === 'money' ? '金額與空白值處理' : type === 'date' && mode === 'set' && !isDayOfMonth(rule.operator) ? (canIncludeBlank ? '貼上日期與空白值處理' : '貼上日期清單') : '空白值處理') +
        (activeNotes.length ? '（' + activeNotes.join('、') + '）' : '') + '</summary><div class="value-editor__aux">' + extra.join('') + '</div></details>' : '') + '</div>';
  }
  function bind(root, rule, state, changed) {
    if (!root) { return; }
    function fitLists() {
      root.querySelectorAll('textarea.value-editor__list').forEach(function (input) {
        if (!input.getClientRects().length) { return; }
        input.style.height = 'auto';
        input.style.height = Math.min(160, Math.max(36, input.scrollHeight)) + 'px';
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
          // 分錄性質（借貸別、人工／自動）不是 fieldValue 的欄位：交回呼叫端換成對應的規則型別。
          if (!selected) { changed('pseudo', control.value); return; }
          delete rule.field; delete rule.fieldId;
          rule[selected.extra ? 'fieldId' : 'field'] = selected.id;
          rule.operator = selected.type === 'date' || !selected.extra && ['createBy', 'approveBy', 'accNum'].indexOf(selected.id) >= 0 ? 'in' : selected.type === 'text' ? 'contains' : 'equals';
          rule.amountBasis = selected.extra ? 'signed' : 'absolute';
          rule.value = ''; rule.from = ''; rule.to = ''; rule.values = []; rule.includeBlank = false;
        } else if (key === 'values') { rule.values = delimitedList(rule, state) ? splitDelimited(control.value) : control.value.split(/\r?\n/); }
        else if (key === 'daysOfMonth') { rule.values = splitDelimited(control.value); }
        else if (key === 'includeBlank') { rule.includeBlank = control.checked; }
        else {
          if (key === 'operator' && carrier(control.value, fieldType(rule, state)) !== carrier(rule.operator, fieldType(rule, state))) { rule.values = []; }
          rule[key] = control.value;
        }
        changed(structural);
        var notice = root.querySelector('[data-value-warning]');
        if (notice) { notice.textContent = warning(rule, state); notice.hidden = !notice.textContent; }
        if (key === 'values') { refreshChips(); fitLists(); }
      });
    });
    function refreshChips() {
      var chips = root.querySelector('[data-selected-dates]');
      if (!chips) { return; }
      chips.innerHTML = values(rule, state).map(function (date, index) {
        return '<button type="button" class="scenario-pill" data-date-remove="' + index + '" aria-label="移除日期 ' + Ui.esc(date) + '">' + Ui.esc(date) + ' ×</button>';
      }).join('');
      chips.querySelectorAll('[data-date-remove]').forEach(function (button) {
        button.addEventListener('click', function () { rule.values = values(rule, state); rule.values.splice(Number(button.dataset.dateRemove), 1); changed(true); });
      });
    }
    refreshChips();
    var clear = root.querySelector('[data-values-clear]');
    if (clear) { clear.addEventListener('click', function () { rule.values = []; changed(true); }); }
    var opener = root.querySelector('[data-calendar-open]');
    if (opener) { opener.addEventListener('click', function () { calendar(opener, rule, state, changed); }); }
    bindAccountTree(root, rule, state, changed);
  }
  function calendar(opener, rule, state, changed) {
    var selected = values(rule, state), originalFocus = opener.getAttribute('data-focus-key');
    var initial = selected.find(function (value) { return /^\d{4}-\d{2}-\d{2}$/.test(value); }) ||
      (state.project && state.project.periodStart) || iso(new Date());
    var parts = initial.split('-').map(Number), focus = day(parts[0], parts[1], parts[2]);
    var dialog = document.createElement('dialog'); dialog.className = 'date-multiselect';
    dialog.setAttribute('aria-label', '選擇多個日期');
    document.body.appendChild(dialog);
    function close() { dialog.close(); dialog.remove(); if (opener.isConnected) { opener.focus(); } }
    function draw() {
      var year = focus.getUTCFullYear(), month = focus.getUTCMonth() + 1, first = day(year, month, 1);
      var end = day(year, month + 1, 0).getUTCDate(), cells = [];
      for (var pad = 0; pad < first.getUTCDay(); pad++) { cells.push('<span role="gridcell"></span>'); }
      for (var n = 1; n <= end; n++) {
        var date = iso(day(year, month, n)), on = selected.indexOf(date) >= 0;
        cells.push('<button type="button" role="gridcell" data-calendar-date="' + date + '" aria-label="' + date +
          '" aria-selected="' + on + '" tabindex="' + (n === focus.getUTCDate() ? '0' : '-1') + '" class="' + (on ? 'is-selected' : '') + '">' + n + '</button>');
      }
      while (cells.length % 7) { cells.push('<span role="gridcell"></span>'); }
      var calendarRows = '';
      for (var offset = 0; offset < cells.length; offset += 7) { calendarRows += '<div role="row">' + cells.slice(offset, offset + 7).join('') + '</div>'; }
      dialog.innerHTML = '<div class="date-multiselect__head"><button type="button" data-month="-1" aria-label="上一個月">‹</button>' +
        '<strong aria-live="polite">' + year + ' 年 ' + month + ' 月</strong><button type="button" data-month="1" aria-label="下一個月">›</button></div>' +
        '<div class="date-multiselect__week" aria-hidden="true">' + ['日','一','二','三','四','五','六'].map(function (text) { return '<span>' + text + '</span>'; }).join('') + '</div>' +
        '<div role="grid" aria-label="' + year + ' 年 ' + month + ' 月" aria-multiselectable="true" class="date-multiselect__grid">' + calendarRows + '</div>' +
        '<p role="status">已選 ' + selected.length + ' 個日期；可切換月份繼續選取。</p><p class="rule-field__hint">方向鍵移動，空白鍵選取；Page Up 和 Page Down 換月。</p>' +
        '<p data-calendar-error role="alert"></p><div class="panel__actions"><button type="button" data-calendar-cancel>取消</button><button type="button" class="btn" data-calendar-done>完成選取</button></div>';
      dialog.querySelectorAll('[data-month]').forEach(function (button) { button.onclick = function () {
        var next = day(year, month + Number(button.dataset.month), 1);
        if (next.getUTCFullYear() < 1 || next.getUTCFullYear() > 9999) { return; }
        focus = next; draw();
      }; });
      dialog.querySelectorAll('[data-calendar-date]').forEach(function (button) {
        button.onclick = function () {
          var date = button.dataset.calendarDate, index = selected.indexOf(date);
          if (index >= 0) { selected.splice(index, 1); }
          else if (selected.length < Ui.TYPED_SET_MAX_VALUES) { selected.push(date); }
          else { dialog.querySelector('[data-calendar-error]').textContent = '最多選取 ' + Ui.TYPED_SET_MAX_VALUES + ' 個不同日期，請先移除不需要的日期。'; return; }
          var p = date.split('-').map(Number); focus = day(p[0], p[1], p[2]); draw();
        };
        button.onkeydown = function (event) {
          var delta = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 }[event.key], next;
          if (delta) { next = day(year, month, focus.getUTCDate() + delta); }
          else if (event.key === 'Home' || event.key === 'End') { next = day(year, month, focus.getUTCDate() + (event.key === 'Home' ? -focus.getUTCDay() : 6 - focus.getUTCDay())); }
          else if (event.key === 'PageUp' || event.key === 'PageDown') { next = day(year, month + (event.key === 'PageUp' ? -1 : 1) * (event.shiftKey ? 12 : 1), 1); }
          if (next && next.getUTCFullYear() >= 1 && next.getUTCFullYear() <= 9999) { event.preventDefault(); focus = next; draw(); }
        };
      });
      dialog.querySelector('[data-calendar-cancel]').onclick = close;
      dialog.querySelector('[data-calendar-done]').onclick = function () {
        rule.values = selected.slice().sort(); close(); changed(true);
        if (global.JetFocus) { global.JetFocus.defer(function () { return document.querySelector('[data-focus-key="' + originalFocus + '"]'); }); }
      };
      var target = dialog.querySelector('[data-calendar-date="' + iso(focus) + '"]'); if (target) { target.focus(); }
    }
    dialog.addEventListener('cancel', function (event) { event.preventDefault(); close(); });
    draw(); dialog.showModal();
    var target = dialog.querySelector('[data-calendar-date="' + iso(focus) + '"]'); if (target) { target.focus(); }
  }
  function problem(rule, state) {
    var selected = field(rule, state);
    if (!selected) { return '重新選擇欄位，或回到第三步確認配對'; }
    var value = wire(rule, state), mode = carrier(rule.operator, selected.type);
    if (operators[selected.type].indexOf(rule.operator) < 0) { return selected.label + '需選擇比較方式'; }
    if (rule.drCr && ['debit', 'credit'].indexOf(rule.drCr) < 0) { return '請重新選擇分錄方向'; }
    if (isMonthWindow(rule.operator)) {
      return /^\d{1,2}$/.test(value.value.trim()) && Number(value.value) >= 1 && Number(value.value) <= 31 ? '' : '每月月初或月底天數只能是 1 到 31 的整數';
    }
    if (isDayOfMonth(rule.operator)) {
      var days = value.values;
      if (!days.length) { return selected.label + '請填入每月幾日（1 到 31）'; }
      if (days.some(function (item) { return !/^\d{1,2}$/.test(item) || Number(item) < 1 || Number(item) > 31; })) { return selected.label + '的每月幾日只能是 1 到 31，重複的會自動合併'; }
      return '';
    }
    var inputs = mode === 'set' ? value.values : mode === 'range' ? [value.from, value.to] : mode === 'value' ? [value.value] : [];
    // Count exactly what wire() sends; never guess case equivalence in JavaScript.
    if (mode === 'set' && (!inputs.length || inputs.length > Ui.TYPED_SET_MAX_VALUES)) { return selected.label + '請填入 1 到 ' + Ui.TYPED_SET_MAX_VALUES + ' 個不同值'; }
    if (inputs.some(function (input) { return !input.trim(); })) { return selected.label + '請填好條件值'; }
    if (isTail(rule.operator)) {
      var tails = splitDelimited(value.value);
      if (!tails.length || tails.length > Ui.TYPED_SET_MAX_VALUES || tails.some(function (item) { return !/^\d{1,12}$/.test(item); })) {
        return '尾數請填入 1 到 ' + Ui.TYPED_SET_MAX_VALUES + ' 組純數字，每組 1 到 12 位';
      }
      return '';
    }
    if (selected.type === 'money') {
      if (inputs.some(function (input) { return moneyNumber(input) === null; })) { return '金額格式無效，請填數字，可保留千分位逗號、小數與正負號'; }
      if (mode === 'range' && moneyNumber(value.from) > moneyNumber(value.to)) { return '金額區間起點大於終點，請修正'; }
    }
    if (selected.type === 'date' && inputs.some(function (input) {
      if (!/^\d{4}-\d{2}-\d{2}$/.test(input)) {
        // Recognizable invalid dates can be corrected here. Other invariant-culture formats
        // are forwarded unchanged to DateNormalizer; browser parsing must not redefine GL dates.
        return /^\d{1,4}[-/.]\d{1,2}[-/.]\d{1,2}$/.test(input) || moneyNumber(input) !== null;
      }
      var p = input.split('-').map(Number); return p[0] < 1 || iso(day(p[0], p[1], p[2])) !== input;
    })) { return selected.label + '含無效日期，請依提示格式修正'; }
    if (selected.type === 'date' && mode === 'range' && /^\d{4}-\d{2}-\d{2}$/.test(value.from) && /^\d{4}-\d{2}-\d{2}$/.test(value.to) && value.from > value.to) { return selected.label + '起點晚於終點，請修正日期區間'; }
    return '';
  }
  function create(type) {
    return { type: 'fieldValue', join: 'AND', field: type === 'date' ? 'postDate' : type === 'money' ? 'amount' : 'description',
      operator: type === 'date' ? 'in' : type === 'text' ? 'contains' : 'equals', values: [], value: '', includeBlank: false, amountBasis: 'absolute', __valueType: type };
  }
  Ui.FilterValues = { create: create, render: render, bind: bind, wire: wire, summary: summary, field: field, fields: fields, groupedFields: groupedFields, fieldOptionsHtml: fieldOptionsHtml, values: values, normalizeDate: normalizeDate, splitDelimited: splitDelimited, problem: problem, isDayOfMonth: isDayOfMonth };
})(window);
