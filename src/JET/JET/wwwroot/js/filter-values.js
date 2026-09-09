/* Value editors only build conditions. Matching and blank/category decisions belong to the backend. */
(function (global) {
  'use strict';
  var Ui = global.JetUi;
  // 標籤鏡像 Domain FieldValueConditions.Labels（FilterAstFrontendContractTests 逐鍵守衛）。
  var labels = { equals: '等於', notEquals: '不等於', contains: '包含任一文字', notContains: '不包含任何文字',
    startsWith: '開頭符合', endsWith: '結尾符合', in: '符合清單任一值', notIn: '不在清單中',
    on: '指定日期', before: '早於', onOrBefore: '當日或以前', after: '晚於', onOrAfter: '當日或以後',
    dayOfMonthIn: '每月幾日屬於', dayOfMonthNotIn: '每月幾日不屬於',
    greaterThan: '大於', greaterThanOrEqual: '大於或等於', lessThan: '小於', lessThanOrEqual: '小於或等於',
    between: '介於', notBetween: '不介於', isBlank: '空白', isNotBlank: '非空白' };
  var operators = {
    text: ['contains', 'notContains', 'equals', 'notEquals', 'startsWith', 'endsWith', 'in', 'notIn', 'isBlank', 'isNotBlank'],
    date: ['in', 'notIn', 'dayOfMonthIn', 'dayOfMonthNotIn', 'on', 'notEquals', 'before', 'onOrBefore', 'after', 'onOrAfter', 'between', 'notBetween', 'isBlank', 'isNotBlank'],
    money: ['equals', 'notEquals', 'greaterThan', 'greaterThanOrEqual', 'lessThan', 'lessThanOrEqual', 'between', 'notBetween', 'in', 'notIn', 'isBlank', 'isNotBlank']
  };
  function isDayOfMonth(op) { return op === 'dayOfMonthIn' || op === 'dayOfMonthNotIn'; }
  function carrier(op, type) {
    if (/^(isBlank|isNotBlank)$/.test(op)) { return 'none'; }
    if (/^(notBetween|between)$/.test(op)) { return 'range'; }
    if (/^(notIn|in)$/.test(op) || isDayOfMonth(op)) { return 'set'; }
    if (type === 'text' && /^(contains|notContains)$/.test(op)) { return 'set'; }
    return 'value';
  }
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
  function normalizeDate(raw) {
    var text = String(raw).trim();
    var match = /^(\d{4})[-/](\d{2})[-/](\d{2})$/.exec(text) || /^(\d{4})(\d{2})(\d{2})$/.exec(text);
    if (!match || Number(match[1]) < 1) { return text; }
    var expected = match[1] + '-' + match[2] + '-' + match[3];
    return iso(day(Number(match[1]), Number(match[2]), Number(match[3]))) === expected ? expected : text;
  }
  function values(rule, state) {
    var selected = field(rule, state);
    return Array.from(new Set((rule.values || []).map(function (value) {
      if (isDayOfMonth(rule.operator)) { return String(value).trim(); }
      return selected && selected.type === 'date' ? normalizeDate(value) : String(value).trim();
    }).filter(function (value) { return value.length > 0; })));
  }
  function wire(rule, state) {
    var selected = field(rule, state);
    var result = { type: 'fieldValue', join: rule.join || 'AND', operator: rule.operator, includeBlank: blankMatches(rule) };
    if (rule.fieldId) { result.fieldId = rule.fieldId; } else { result.field = rule.field; }
    var mode = carrier(rule.operator, fieldType(rule, state));
    function operand(value) { return selected && selected.type === 'date' && !isDayOfMonth(rule.operator) ? normalizeDate(value || '') : String(value || ''); }
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
    if (selected && selected.type === 'money' && mode !== 'none') { fieldLabel += value.amountBasis === 'absolute' ? '絕對值' : '含正負號'; }
    var text = fieldLabel + ' ' + (labels[rule.operator] || '尚未選擇比較方式');
    if (mode === 'value') { text += '「' + value.value + '」'; }
    if (mode === 'range') { text += '「' + value.from + '」至「' + value.to + '」'; }
    if (mode === 'set') { text += '「' + value.values.join('、') + '」'; }
    if (mode !== 'none' && (!compact || blankMatches(rule))) { text += blankMatches(rule) ? '；空白也符合' : '；空白不列入'; }
    return text;
  }
  // 欄位下拉：同型別欄位、其他型別欄位與「分錄性質」（借貸別、人工／自動）都列出來，審計員在同一個下拉
  // 換欄位就換條件；換到其他型別由本模組重設運算子，換到分錄性質交回呼叫端換成對應的規則型別。
  function fieldOptionsHtml(state, selectedId, pseudoFields) {
    var all = fields(state);
    var groups = [
      { label: '日期', type: 'date' }, { label: '文字', type: 'text' }, { label: '金額', type: 'money' }
    ].map(function (group) {
      var items = all.filter(function (item) { return item.type === group.type; });
      if (!items.length) { return ''; }
      return '<optgroup label="' + group.label + '">' + items.map(function (item) {
        return '<option value="' + Ui.esc(item.id) + '"' + (item.id === selectedId ? ' selected' : '') + '>' + Ui.esc(item.label) + (item.extra ? '（額外欄位）' : '') + '</option>';
      }).join('') + '</optgroup>';
    }).join('');
    var pseudo = (pseudoFields || []).length
      ? '<optgroup label="分錄性質">' + pseudoFields.map(function (item) {
          return '<option value="' + Ui.esc(item.id) + '"' + (item.id === selectedId ? ' selected' : '') + '>' + Ui.esc(item.label) + '</option>';
        }).join('') + '</optgroup>'
      : '';
    return groups + pseudo;
  }
  // Presentation groups share an input intent; the original operator remains authoritative.
  var editorViews = new WeakMap();
  function editorView(rule) {
    if (!editorViews.has(rule)) { editorViews.set(rule, { optionsOpen: false, periodEnd: false, days: 7 }); }
    return editorViews.get(rule);
  }
  var commonModes = {
    date: [
      { key: 'dates', label: '指定日期', yes: 'in', no: 'notIn', aliases: ['on', 'notEquals'] },
      { key: 'range', label: '日期區間', yes: 'between', no: 'notBetween' },
      { key: 'monthDays', label: '每月幾日', yes: 'dayOfMonthIn', no: 'dayOfMonthNotIn' },
      { key: 'before', label: '早於日期', yes: 'before', aliases: ['onOrBefore'] },
      { key: 'after', label: '晚於日期', yes: 'after', aliases: ['onOrAfter'] },
      { key: 'blank', label: '空白日期', yes: 'isBlank', no: 'isNotBlank' }
    ],
    text: [
      { key: 'contains', label: '包含關鍵字', yes: 'contains', no: 'notContains' },
      { key: 'exact', label: '完整內容相同', yes: 'in', no: 'notIn', aliases: ['equals', 'notEquals'] },
      { key: 'starts', label: '開頭符合', yes: 'startsWith' },
      { key: 'ends', label: '結尾符合', yes: 'endsWith' },
      { key: 'blank', label: '空白內容', yes: 'isBlank', no: 'isNotBlank' }
    ],
    money: [
      { key: 'exact', label: '指定金額', yes: 'in', no: 'notIn', aliases: ['equals', 'notEquals'] },
      { key: 'range', label: '金額區間', yes: 'between', no: 'notBetween' },
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
  function render(rule, state, focusKey, pseudoFields) {
    var selected = field(rule, state), type = fieldType(rule, state), mode = carrier(rule.operator, type);
    var choice = selectedMode(rule, state), view = editorView(rule);
    var main = '';
    if (choice) {
      main += choice.no ? '<select data-value-polarity aria-label="保留或排除"><option value="keep"' + (!excluded(rule, choice) ? ' selected' : '') +
        '>保留</option><option value="exclude"' + (excluded(rule, choice) ? ' selected' : '') + '>排除</option></select>' : '<span>保留</span>';
    } else { main += '<span class="value-advanced-current">' + Ui.esc(labels[rule.operator] || '請設定比較方式') + '</span>'; }
    main += '<select data-value-kind aria-label="篩選方式">' + (!choice ? '<option value="advanced" selected>其他比較方式</option>' : '') +
      commonModes[type].map(function (item) { return '<option value="' + item.key + '"' + (choice === item && !view.periodEnd ? ' selected' : '') + '>' + item.label + '</option>'; }).join('') +
      (type === 'date' && state.project && state.project.periodEnd ? '<option value="periodEnd"' + (view.periodEnd ? ' selected' : '') + '>查核期末最後幾天</option>' : '') + '</select>';
    function input(key, label) { return '<input type="' + (type === 'date' ? 'date' : 'text') + '" data-value-key="' + key + '" aria-label="' + label + '" placeholder="' + label + '" value="' + Ui.esc(rule[key] || '') + '">'; }
    var extra = [];
    var canIncludeBlank = mode !== 'none' && !(rule.field === 'postDate' && !rule.fieldId);
    if (!selected) { main += '<p class="form-notice">此欄位目前未配對，請回第三步確認，或移除後重新加入。</p>'; }
    if (mode === 'value') { main += input('value', type === 'money' ? '金額' : '條件值'); }
    if (view.periodEnd) {
      main += '<input type="number" data-period-end-days min="1" max="366" value="' + view.days + '" aria-label="期末最後幾天"><span>天</span>' +
        '<p class="value-period-range">' + Ui.esc(rule.from || '') + ' 至 ' + Ui.esc(rule.to || '') + '（含起迄日）</p>';
    } else if (mode === 'range') { main += '<div class="value-editor__range" aria-label="篩選區間">' + input('from', '起點（包含）') + '<span class="rule-row__sep">至</span>' + input('to', '終點（包含）') + '</div>'; }
    if (choice && ['above', 'below', 'before', 'after'].indexOf(choice.key) >= 0) {
      main += '<label><input type="checkbox" data-value-inclusive' + (/OrEqual$|^onOr/.test(rule.operator) ? ' checked' : '') + '>' + (type === 'date' ? '含當天' : '含門檻金額') + '</label>';
    }
    if (mode === 'set' && isDayOfMonth(rule.operator)) {
      main += '<input type="text" class="value-editor__days" data-value-key="daysOfMonth" inputmode="numeric" aria-label="每月幾日（逗號分隔，1 到 31）" placeholder="例如：28,31" value="' + Ui.esc(values(rule, state).join(',')) + '"><span>日</span>';

    } else if (mode === 'set') {
      var current = values(rule, state);
      var listHint = type === 'date' ? '每行一個日期' : type === 'text' && /^(contains|notContains)$/.test(rule.operator) ? '每行一個關鍵字' : '每行一個值';
      var list = '<textarea rows="1" class="value-editor__list" data-value-key="values" aria-label="' + listHint + '" placeholder="' + listHint + '">' + Ui.esc((rule.values || []).join('\n')) + '</textarea>';
      if (type === 'date') {
        main += '<button type="button" class="btn btn--ghost btn--tiny" data-calendar-open data-focus-key="' + focusKey + '">選日期</button>' +
          '<div class="selected-dates" data-selected-dates aria-label="已選日期"></div>';
        extra.push('<label>貼上日期清單' + list + '</label>');
      } else { main += list; }
      extra.push('<p class="rule-field__hint">最多 100 個不同值，空行不列入。' + (type === 'date' ? '日期可用 2025-08-01、2025/08/01 或 20250801。' : '保留文字前置零；金額千分位逗號不拆開。') + '</p>');
      if (current.length) { extra.push('<button type="button" class="btn btn--ghost btn--tiny" data-values-clear>清空所有值</button>'); }
    }
    if (type === 'money') {
      var basis = rule.amountBasis || (selected && selected.extra ? 'signed' : 'absolute');
      extra.push('<label>金額比較基準<select data-value-key="amountBasis" aria-label="金額比較基準"><option value="absolute"' + (basis !== 'signed' ? ' selected' : '') + '>絕對值（不分借貸）</option><option value="signed"' + (basis === 'signed' ? ' selected' : '') + '>含正負號</option></select></label>');
    }
    if (canIncludeBlank) {
      extra.push('<label class="value-blank"><input type="checkbox" data-value-key="includeBlank"' + (blankMatches(rule) ? ' checked' : '') + '>' +
        (type === 'date' ? '沒有日期的分錄也列入結果' : '此欄位空白的分錄也列入結果') + '</label>');
    }
    var activeNotes = [];
    if (canIncludeBlank && blankMatches(rule)) { activeNotes.push('含空白'); }
    if (type === 'money' && rule.amountBasis === 'signed') { activeNotes.push('含正負號'); }
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
        var key = control.getAttribute('data-value-key'), structural = key === 'field' || key === 'operator' || key === 'includeBlank' || key === 'amountBasis';
        if (key === 'field') {
          var selected = fields(state).find(function (item) { return item.id === control.value; });
          // 分錄性質（借貸別、人工／自動）不是 fieldValue 的欄位：交回呼叫端換成對應的規則型別。
          if (!selected) { changed('pseudo', control.value); return; }
          delete rule.field; delete rule.fieldId;
          rule[selected.extra ? 'fieldId' : 'field'] = selected.id;
          rule.operator = selected.type === 'date' ? 'in' : selected.type === 'text' ? 'contains' : 'equals';
          rule.amountBasis = selected.extra ? 'signed' : 'absolute';
          rule.value = ''; rule.from = ''; rule.to = ''; rule.values = []; rule.includeBlank = false;
        } else if (key === 'values') { rule.values = control.value.split(/\r?\n/); }
        else if (key === 'daysOfMonth') { rule.values = control.value.split(/[,\s，、]+/); }
        else if (key === 'includeBlank') { rule.includeBlank = control.checked; }
        else {
          if (key === 'operator' && carrier(control.value, fieldType(rule, state)) !== carrier(rule.operator, fieldType(rule, state))) { rule.values = []; }
          rule[key] = control.value;
        }
        changed(structural);
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
          else if (selected.length < 100) { selected.push(date); }
          else { dialog.querySelector('[data-calendar-error]').textContent = '最多選取 100 個不同日期，請先移除不需要的日期。'; return; }
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
    if (isDayOfMonth(rule.operator)) {
      var days = value.values;
      if (!days.length) { return selected.label + '請填入每月幾日（1 到 31）'; }
      if (days.some(function (item) { return !/^\d{1,2}$/.test(item) || Number(item) < 1 || Number(item) > 31; })) { return selected.label + '的每月幾日只能是 1 到 31，重複的會自動合併'; }
      return '';
    }
    var inputs = mode === 'set' ? value.values : mode === 'range' ? [value.from, value.to] : mode === 'value' ? [value.value] : [];
    var uniqueCount = new Set(inputs.map(function (input) { return selected.type === 'text' ? input.toUpperCase() : input; })).size;
    if (mode === 'set' && (!inputs.length || selected.type !== 'money' && uniqueCount > 100)) { return selected.label + '請填入 1 到 100 個不同值'; }
    if (inputs.some(function (input) { return !input.trim(); })) { return selected.label + '請填好條件值'; }
    if (selected.type === 'date' && inputs.some(function (input) {
      if (!/^\d{4}-\d{2}-\d{2}$/.test(input)) { return true; }
      var p = input.split('-').map(Number); return p[0] < 1 || iso(day(p[0], p[1], p[2])) !== input;
    })) { return selected.label + '含無效日期，請依提示格式修正'; }
    if (selected.type === 'date' && mode === 'range' && value.from > value.to) { return selected.label + '起點晚於終點，請修正日期區間'; }
    return '';
  }
  function create(type) {
    return { type: 'fieldValue', join: 'AND', field: type === 'date' ? 'postDate' : type === 'money' ? 'amount' : 'description',
      operator: type === 'date' ? 'in' : type === 'text' ? 'contains' : 'equals', values: [], value: '', includeBlank: false, amountBasis: 'absolute', __valueType: type };
  }
  Ui.FilterValues = { create: create, render: render, bind: bind, wire: wire, summary: summary, field: field, fields: fields, fieldOptionsHtml: fieldOptionsHtml, values: values, normalizeDate: normalizeDate, problem: problem, isDayOfMonth: isDayOfMonth };
})(window);
