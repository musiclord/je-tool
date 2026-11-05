/*
  第五步：進階條件篩選 —— 條件列與摘要文字。
  條件組的畫法與連接方式、每一條條件列的控制項與摘要標籤、整個情境的讀回文字。只組裝與渲染，不計算規則。
  第五步分成五個檔案，說明見 filter-step.js 開頭；各檔把自己的函式掛在 JetUi.FilterStep，其他檔案透過它呼叫。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;
  // 第五步五個檔案共用的函式與檢視狀態；各檔在結尾把自己的函式掛上去。
  var FilterStep = Ui.FilterStep = Ui.FilterStep || {};
  // 其他檔案定義的函式：呼叫時才去 FilterStep 取，所以和載入順序無關。
  function forward(name) { return function () { return FilterStep[name].apply(null, arguments); }; }
  var categoryPickerView = forward('categoryPickerView');
  var motionKey = forward('motionKey');
  var groupMotionKey = forward('groupMotionKey');
  var keepMotionKey = forward('keepMotionKey');
  var requirementNote = forward('requirementNote');
  var ownRequirementNote = forward('ownRequirementNote');
  var isPatternMenuCommonType = forward('isPatternMenuCommonType');
  var availableCommonTypes = forward('availableCommonTypes');
  var rdeFieldOptions = forward('rdeFieldOptions');
  var rdeFieldValueType = forward('rdeFieldValueType');
  var availablePrescreenKeys = forward('availablePrescreenKeys');
  var groupCombinator = forward('groupCombinator');
  var scenarioJoin = forward('scenarioJoin');
  var activeEditableGroup = forward('activeEditableGroup');
  var setActiveGroup = forward('setActiveGroup');
  var refreshAutomaticMetadata = forward('refreshAutomaticMetadata');
  var refreshConditionKeys = forward('refreshConditionKeys');
  var exampleListHtml = forward('exampleListHtml');
  var disclosureAttributes = forward('disclosureAttributes');
  var savedFeedbackText = forward('savedFeedbackText');
  var softRefreshGate = forward('softRefreshGate');
  var softRefreshReadback = forward('softRefreshReadback');
  var softExpirePreviewPane = forward('softExpirePreviewPane');
  // 其他檔案定義的常數：只讀，定義它的檔案先載入。
  var EMPTY_FIRST_GROUP = FilterStep.EMPTY_FIRST_GROUP;
  var UNMAPPED_OPTION_SUFFIX = FilterStep.UNMAPPED_OPTION_SUFFIX;
  var RETIRED_TYPE_NOTE = FilterStep.RETIRED_TYPE_NOTE;
  var COMMON_CARD_KEY = FilterStep.COMMON_CARD_KEY;

  // 「選取中」換到別條時收回去再拿掉。
  function dropRuleBadge(badge) {
    if (global.JetMotion && typeof global.JetMotion.fold === 'function') { global.JetMotion.fold(badge, function () { badge.remove(); }); } else { badge.remove(); }
  }

  // 條件列的三個家族：畫面只用家族分類，wire 型別維持原樣（後端不動）。
  var RULE_FAMILIES = {
    field: { label: '欄位', types: ['fieldValue', 'drCrOnly', 'manualAuto'] },
    account: { label: '科目', types: ['accountSide', 'specialAccountCategoryPair', 'accountPair'] },
    compound: { label: '條件組合', types: ['group', 'voucher'] },
    pattern: { label: '樣態', types: ['prescreen', 'entityFrequency',
      'revenueDebitNearQuarterEnd', 'revenueWithoutNormalCounterpart', 'manualRevenueEntry', 'preparerEqualsApprover'] },
    legacy: { label: '舊式', types: ['text', 'textSet', 'dateRange', 'numRange', 'typed'] }
  };

  var PATTERN_PARAM_TYPES = [
    { value: 'entityFrequency', label: '科目與人員統計' }
  ];

  // 樣態下拉的「自訂條件」選項。兩種舊式筆數條件已拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），
  // 已儲存的情境若還有它們，選單和其他認不得的類型一樣寫停用的說明。
  function patternTypeOptions() {
    return PATTERN_PARAM_TYPES;
  }

  function ruleFamily(rule) {
    return Object.keys(RULE_FAMILIES).filter(function (key) { return RULE_FAMILIES[key].types.indexOf(rule.type) >= 0; })[0] || 'legacy';
  }

  /* ============================================================================
     區塊 3：建立篩選情境（彙總調整）— 統一條件建構器（單一介面、把分組融入，無模式切換）。
     情境＝一個或多個「條件組(set)」，每組是一塊托盤，條件寫在托盤上的白紙。AND、OR 一律放在被連接的兩者之間的分隔線上
     （裁定 2026-11-05-16）：組內兩條條件之間、兩組之間與兩條子條件之間都是一條橫線，切換和條件標題、「第 N 組」同一條左緣。
     一組一種連接方式，組內第一條分隔線是這一組的切換，後面的分隔線只寫同一個字；要混就再開組，組內可用「子條件組」再組合一層。
     新手層：教學空狀態＋行內布林 read-back＋4 拍提示；名稱/動機依條件自動帶入，留白時由後端補上同一段文字（裁定 2026-10-29-16）。
     兩層 AST 不變；空群組於 toWireScenario 送出時略過（建到一半不報錯）。
     ============================================================================ */

  // 2026-11-03 以前非營業日卡片加入的括號：只有一條排除補班日的日期條件，讀回「非營業日」。卡片現在直接加入那一條條件
  //（裁定 2026-11-03-09），已儲存情境裡的括號照舊能讀；舊的週末或假日 AST 也不改寫。
  function isNonBusinessDayBracket(rule) {
    var children = rule && rule.type === 'group' ? rule.conditions || [] : [];
    return children.length === 1 && children[0].type === 'fieldValue' && children[0].field === 'glDate' &&
      !children[0].fieldId && children[0].operator === 'isNonBusinessDay' && !children[0].includeBlank && !children[0].drCr;
  }

  function isLegacyWeekendHolidayBracket(rule) {
    var children = rule && rule.type === 'group' ? rule.conditions || [] : [];
    if (children.length !== 2 || effectiveConditionJoin(children[1]) !== 'OR') { return false; }
    var keys = children.map(function (child) { return child.type === 'prescreen' ? child.prescreenKey : null; }).sort();
    return keys[0] === 'holidayPosting' && keys[1] === 'weekendPosting';
  }

  // 一塊托盤（一個條件組）：組標頭是「第 N 組」與組的操作，下面的白紙是條件清單，兩條條件之間是寫著 AND 或 OR 的分隔線。
  // 標頭在單組時也顯示（一致的閱讀順序）；多組時多「已選取」（只影響條件總覽加入的落點）與移除。
  // 組內與組間的連接是 AND、OR 兩格的分段切換（裁定 2026-10-30-32：中文說法太長，審計員也看得懂 AND 與 OR，所以只寫 AND 與 OR）。
  // 切換前面不寫「組內條件」「組與組之間」，連接的是什麼由版面呈現（裁定 2026-11-01-07）；螢幕報讀仍讀 label 寫的名稱。
  // 每組各一組單選鈕，name 用組號區分；選中的一格由 app.js 的滑動選取帶過去。
  function joinToggleHtml(attributes, name, value, label) {
    return '<span class="join-toggle" role="radiogroup" aria-label="' + label + '">' + ['AND', 'OR'].map(function (option) {
      return '<label class="join-toggle__option"><input type="radio" name="' + name + '" value="' + option + '" ' + attributes +
        (value === option ? ' checked' : '') + '><span>' + option + '</span></label>';
    }).join('') + '</span>';
  }

  // 兩條條件之間的分隔線（裁定 2026-11-05-16）。第一條分隔線是這一組 AND、OR 的分段切換；後面的分隔線只寫同一個字，
  // 一組一種連接方式（要混用時開子條件組）。分隔線跟著下面那一條條件長出來、收起來；舊情境裡同組混用的連接，字照每條條件的 join 寫。
  // 原本切換在組標頭，條件左側 44px 的欄位逐條寫連接詞，同一件事有兩個位置。
  function ruleJoinHtml(rule, gi, setNumber, ri, comb) {
    var key = 'join-' + motionKey(rule, 'rule');
    if (ri === 1) {
      return '<div class="rule-join rule-join--switch" data-motion-key="' + key + '">' +
        joinToggleHtml('data-set-combinator data-gi="' + gi + '"', 'set-combinator-' + gi, comb, '第 ' + setNumber + ' 組條件之間的關係') + '</div>';
    }
    return '<div class="rule-join" data-motion-key="' + key + '" aria-hidden="true"><span class="rule-join__word">' + effectiveConditionJoin(rule) + '</span></div>';
  }

  function setWellHtml(group, gi, setNumber, multi, isActive) {
    var comb = group && group.conditions.length ? groupCombinator(group) : 'AND';
    // 組標頭的按鈕只寫名稱，前面放灰色加號，整張傳票條件寫成「整張傳票」（裁定 2026-10-30-32）。
    var head = '<div class="filter-group-heading"><strong>第 ' + setNumber + ' 組' +
      '<span class="filter-group-selected" data-collapsible-inline' + (isActive && multi ? '' : ' hidden') + '>已選取</span></strong>' +
      '<div class="filter-group-actions">' +
      '<button type="button" class="btn btn--ghost btn--tiny btn--add" data-add-compound="group" data-gi="' + gi + '" aria-label="在第 ' + setNumber + ' 組加入子條件組">子條件組</button>' +
      '<button type="button" class="btn btn--ghost btn--tiny btn--add" data-add-compound="voucher" data-gi="' + gi + '" aria-label="在第 ' + setNumber + ' 組加入整張傳票條件">整張傳票</button>' +
      '<button type="button" class="btn btn--ghost btn--tiny" data-action="clear-set" data-gi="' + gi + '" aria-label="清除第 ' + setNumber + ' 組的條件"' +
        (group && group.conditions.length ? '' : ' disabled') + '>清除</button>' +
      (multi ? '<button type="button" class="btn btn--ghost btn--tiny" data-action="remove-set" data-gi="' + gi + '" aria-label="移除第 ' + setNumber + ' 組">移除</button>' : '') +
      '</div></div>';
    var rows = group ? group.conditions.map(function (rule, ri) {
      return (ri ? ruleJoinHtml(rule, gi, setNumber, ri, comb) : '') + ruleRowHtml(rule, group, gi, ri);
    }).join('') : '';
    var wellKey = groupMotionKey(group);
    return '<div class="scenario-flat scenario-set' + (isActive ? ' scenario-set--active' : '') + '" data-motion-key="' + wellKey + '" data-group-index="' + gi + '" tabindex="0" role="group" aria-current="' + isActive + '" aria-label="第 ' + setNumber + ' 組條件">' +
      head + '<div class="scenario-flat__list" data-motion-list="rules">' +
      (rows || '<p class="filter-group-empty" data-motion-key="empty-' + wellKey + '">從條件總覽選條件加入。</p>') + '</div></div>';
  }

  // 條件組之間的連接器：一條橫線，AND、OR 的分段切換接在線上，和「第 N 組」同一條左緣（裁定 2026-11-05-16）。data-set-join 設「所有可編輯群組」的
  // join 為一致值（單一組間運算子）。name 以 gi 唯一，避免 3 組以上時多個連接器共用 name 互相撞群（各連接器顯示同一致值即可）。
  function interSetConnectorHtml(joinValue, gi, motion) {
    return (
      '<div class="set-rail" data-motion-key="rail-' + motion + '">' +
        joinToggleHtml('data-set-join data-set-join-index="' + gi + '"', 'set-join-' + gi, joinValue, '組與組之間的關係') +
      '</div>'
    );
  }

  // 渲染所有「條件組」：每個群組一塊 well（組間插白話連接器）。組號就是草稿中的順序（第 gi + 1 組），
  // 「尚需補齊」的位置說明也用同一個組號。
  function setsHtml(draft) {
    var editable = draft.groups.map(function (g, gi) { return { group: g, gi: gi }; });

    // 還沒有條件組（新草稿）：先畫一塊空的第 1 組，讓「＋」列一開始就在；按下去才真的建組。
    if (editable.length === 0) {
      return setWellHtml(null, draft.groups.length, 1, false, true);
    }

    var multi = editable.length >= 2;
    var active = activeEditableGroup(draft);
    var interJoin = scenarioJoin(draft); // 與 read-back、新組繼承、wire normalize 同一推導點
    var wells = editable.map(function (e, idx) {
      var isFirst = idx === 0;
      var connector = isFirst ? '' : interSetConnectorHtml(draft.__preserveJoins ? effectiveConditionJoin(e.group) : interJoin, e.gi, groupMotionKey(e.group));
      return connector + setWellHtml(e.group, e.gi, idx + 1, multi, e.group === active);
    }).join('');
    return wells;
  }

  // 行內布林：把一組條件文字以指定運算子（AND/OR）相連，運算子上色（opClass）。條件逐一 Ui.esc，
  // 運算子為字面 AND/OR（安全）。
  function exprJoin(labels, op, opClass) {
    var opHtml = ' <span class="expr-op ' + opClass + '">' + (op === 'OR' ? '或' : '且') + '</span> ';
    return labels.map(function (l) { return Ui.esc(l); }).join(opHtml);
  }

  // Raw AST 的第一條 rule.join 不參與左折疊；只有其後 semantic 有效邊同時含 AND/OR 時才逐邊呈現。
  // Mixed branch 跟 typed AST 一樣忽略大小寫/前後空白；uniform fallback 仍沿用既有 groupCombinator，
  // 因此全小寫 uniform join 的 read-back 不在本 slice 被正規化。
  function effectiveConditionJoin(rule) {
    var raw = rule && typeof rule.join === 'string' ? rule.join.trim().toUpperCase() : '';
    return raw === 'OR' ? 'OR' : 'AND';
  }

  function hasMixedEffectiveConditionJoins(group) {
    var joins = group.conditions.slice(1).map(effectiveConditionJoin);
    return joins.indexOf('AND') >= 0 && joins.indexOf('OR') >= 0;
  }

  function groupReadBackExpressionHtml(group, compact) {
    var labels = group ? group.conditions.map(function (r) { return ruleSummaryLabel(r, 0, compact); }) : [];
    if (!hasMixedEffectiveConditionJoins(group)) {
      return exprJoin(labels, groupCombinator(group) === 'OR' ? 'OR' : 'AND', 'expr-op--group');
    }

    var expression = Ui.esc(labels[0]);
    for (var i = 1; i < labels.length; i += 1) {
      var op = effectiveConditionJoin(group.conditions[i]);
      var opHtml = ' <span class="expr-op expr-op--group">' + (op === 'OR' ? '或' : '且') + '</span> ';
      expression = '（' + expression + opHtml + Ui.esc(labels[i]) + '）';
    }
    return expression;
  }

  // 整句回顯（read-back）：句首白話 lead＋行內布林式，把 AND/OR 寫進去（鏡像控制項：OR＝情境層藍粗、
  // AND＝組內灰）。同時可直接作為底稿的條件邏輯。條件文字用 ruleSummaryLabel（index 0 去前綴）。
  function readBackHtml(draft, compact) {
    var exprHtml = readBackExpressionHtml(draft, compact);
    if (!exprHtml) { return ''; }

    return (
      '<p class="scenario-readback">' +
        '<span class="scenario-readback__lead">篩選條件：</span>' +
        '<span class="scenario-readback__expr">' + exprHtml + '</span>' +
      '</p>'
    );
  }

  // 讀回句中的條件式本身；建議動機用它的純文字，不必經過畫面元素。
  function readBackExpressionHtml(draft, compact) {
    var editable = draft.groups;

    // 只看「有條件」的組：剛按〔＋另一組條件〕還沒填的空組不進 read-back，避免懸空運算子（如 … OR AND …）。
    var ne = editable.filter(function (g) { return g.conditions.length > 0; });

    var exprHtml = '';
    if (ne.length === 1) {
      exprHtml = groupReadBackExpressionHtml(ne[0], compact);
    } else if (ne.length >= 2) {
      // Saved joins belong to the following surviving group, just as toWireScenario omits empty groups.
      var sop = draft.__preserveJoins ? effectiveConditionJoin(ne[1]) : scenarioJoin(draft);
      var parts = ne.map(function (g) {
        var inner = groupReadBackExpressionHtml(g, compact);
        return g.conditions.length > 1 && !hasMixedEffectiveConditionJoins(g) ? '（' + inner + '）' : inner;
      });
      if (draft.__preserveJoins && hasMixedEffectiveConditionJoins({ conditions: ne })) {
        exprHtml = parts[0];
        for (var pi = 1; pi < parts.length; pi++) {
          exprHtml = '（' + exprHtml + ' <span class="expr-op expr-op--scenario">' + (effectiveConditionJoin(ne[pi]) === 'OR' ? '或' : '且') + '</span> ' + parts[pi] + '）';
        }
      } else {
        exprHtml = parts.join(' <span class="expr-op expr-op--scenario">' + (sop === 'OR' ? '或' : '且') + '</span> ');
      }
    }
    return exprHtml;
  }

  // 把 Ui.esc 組出的讀回 HTML 轉回純文字：拿掉標籤，還原跳脫字元。
  function readBackPlainText(html) {
    return String(html || '').replace(/<[^>]*>/g, '')
      .replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, '&')
      .trim();
  }

  function scenarioBuilderHtml(draft) {
    function ruleCount(rule) { return 1 + (rule.conditions || []).reduce(function (n, child) { return n + ruleCount(child); }, 0); }
    var totalRules = draft.groups.reduce(function (n, g) { return n + g.conditions.reduce(function (sum, rule) { return sum + ruleCount(rule); }, 0); }, 0);
    var editing = typeof draft.__editingIndex === 'number';
    // 情境範例放在條件組合上方的托盤，和左側條件總覽同一種材質：托盤放可按的鍵，紙上是組出來的條件（裁定 2026-11-03-05）。
    // 托盤預設收起，按標題打開；開合狀態記在 viewState.disclosures，同一次開案內記住（裁定 2026-11-03-19）。
    return '<details class="filter-template-tray" data-motion-key="template-tray"' + disclosureAttributes('templates') + '>' +
        '<summary class="filter-template-tray__title">從情境範例開始</summary>' +
        '<div class="filter-example-list" data-example-list data-motion-key="examples">' + exampleListHtml() + '</div>' +
      '</details>' +
      '<section class="rule-card scenario-builder" data-builder-title tabindex="-1" aria-label="條件組合">' +
      (editing ? '<div class="filter-builder-heading" data-motion-key="editing-heading"><h3 class="rule-card__title" data-bind="editing-scenario-name">編輯情境：' + Ui.esc(draft.name) + '</h3></div>' : '') +
      // 編輯情境時「編輯情境：名稱」是工作面標題，「條件組合」降一層。
      '<div class="filter-builder-heading filter-builder-heading--composition"><div>' +
        (editing ? '<h4 class="filter-entry-title">條件組合</h4>' : '<h3 class="filter-entry-title">條件組合</h3>') + '</div>' +
        '<div class="filter-group-tools" role="group" aria-label="條件組合操作"><button type="button" class="btn btn--ghost btn--add" data-action="add-set" aria-label="新增條件組">條件組</button></div></div>' +
      setsHtml(draft) +
      (totalRules > 1 ? '<section class="filter-readback" data-motion-key="readback" aria-label="完整篩選條件">' + readBackHtml(draft) + '</section>' : '') +
      '<section class="filter-save-panel" data-save-panel data-motion-key="save-panel" aria-label="情境名稱與篩選動機">' +
        '<label class="form__row"><span class="form__label">情境名稱</span>' +
          '<input class="form__input" type="text" data-bind="scenario-name" maxlength="' + Ui.FILTER_MAX_SCENARIO_NAME_LENGTH + '" placeholder="例：摘要異常且金額偏高" value="' + Ui.esc(draft.name) + '"></label>' +
        '<label class="form__row"><span class="form__label">篩選動機說明</span>' +
          '<textarea class="form__input" rows="2" data-bind="scenario-rationale" placeholder="說明保留這組條件的審計理由">' + Ui.esc(draft.rationale) + '</textarea></label>' +
      '</section>' +
      '<p class="form-notice" data-bind="scenario-notice" data-collapsible role="alert" hidden></p>' +
      '<p class="filter-save-feedback" data-motion-key="save-feedback" data-bind="save-feedback" data-collapsible role="status"' + (savedFeedbackText(draft) ? '' : ' hidden') + '>' + savedFeedbackText(draft) + '</p>' +
      '<div class="panel__actions filter-primary-actions" data-motion-key="primary-actions">' +
        '<button type="button" class="btn btn--ghost panel__action--start" data-action="preview-scenario">查看符合的傳票</button>' +
        // 取消緊鄰主要按鈕左邊，和全站按鈕列相同（裁定 2026-10-14-04）。
        (editing ? '<button type="button" class="btn btn--ghost" data-action="save-scenario-copy">另存為新情境</button>' +
          '<button type="button" class="btn btn--ghost" data-action="cancel-edit-scenario">取消編輯</button>' : '') +
        '<button type="button" class="btn" data-action="save-scenario">' + (editing ? '儲存變更' : '儲存情境') + '</button>' +
      '</div></section>';
  }

  // 一條條件＝一句話：家族小標、對象、比較方式、值、移除。列上沒有型別下拉；要換家族就移除再加。
  // 條件列不寫 AND、OR：兩條條件之間的分隔線寫出這一組的連接方式（ruleJoinHtml）。
  function ruleRowHtml(rule, group, gi, ri, insideVoucher) {
    if (rule.type === 'group' || rule.type === 'voucher') { return compoundRuleHtml(rule, gi, ri, insideVoucher, group); }
    var family = ruleFamily(rule);
    // 列標題寫條件的來源，樣態下拉寫是哪一條；常用篩選條件的樣態寫卡片名稱（名詞表「常用篩選條件」一列）。
    var patternHeading = family === 'pattern'
      ? ({ prescreen: '預篩選條件', common: '常用篩選條件' }[patternSource(rule)] ||
          ((patternTypeOptions().find(function (item) { return item.value === rule.type; }) ||
            Ui.FILTER_RULE_TYPES.find(function (item) { return item.value === rule.type; }) || {}).label || '分錄特徵'))
      : '';

    return (
      '<div class="rule-row rule-row--' + family + (FilterStep.selectedRule === rule ? ' rule-row--selected' : '') + '" data-motion-key="' + motionKey(rule, 'rule') + '" data-gi="' + gi + '" data-ri="' + ri + '">' +
        '<div class="filter-rule-heading">' +
          (FilterStep.selectedRule === rule ? '<span class="filter-rule-selected" data-motion="inline">選取中</span>' : '') +
          (rule.type === 'drCrOnly' ? '<strong>借貸別</strong>' : rule.type === 'manualAuto' ? '<strong>人工/自動分錄</strong>' : '') +
          (rule.type === 'accountPair' || rule.type === 'specialAccountCategoryPair' ? '<strong>借貸科目組合</strong>' : '') +
          (rule.type === 'accountSide' ? '<strong>科目分類</strong>' : '') +
          (rule.type === 'fieldValue' ? '<strong>' + Ui.esc(Ui.FilterValues.fieldTitle(rule, Store.getState())) + '</strong>' : '') +
          (patternHeading ? '<strong>' + Ui.esc(patternHeading) + '</strong>' : '') +
          (family === 'legacy' ? '<strong>' + Ui.esc((Ui.FILTER_RULE_TYPES.find(function (item) { return item.value === rule.type; }) || {}).label || '既有篩選條件') + '</strong>' : '') +
          '<button type="button" class="rule-row__remove" data-action="remove-rule" aria-label="移除這條條件" title="移除這條條件">移除</button></div>' +
        '<div class="rule-row__controls">' +
          ruleSubjectHtml(rule, family) + ((rule.type === 'accountPair' || rule.type === 'specialAccountCategoryPair') ? categorySelectionHtml(rule) : '') + ruleControlsHtml(rule, gi, ri, String(ri).indexOf('.') >= 0) + ((rule.type === 'accountPair' || rule.type === 'specialAccountCategoryPair') ? '' : commonDescriptionHtml(rule) + legacyRulePolicyHtml(rule)) +
        '</div>' +
      '</div>'
    );
  }

  // 卡片的說明在加入後的條件列寫一次；同型別的條件不論從哪裡加入都顯示（卡片上不重複，裁定 2026-10-29-09）。
  function commonDescriptionHtml(rule) {
    var item = Ui.FILTER_COMMON_CONDITIONS.find(function (card) {
      return card.key === rule[COMMON_CARD_KEY] || (card.kind === 'type' && card.ref === rule.type);
    });
    return item && item.description ? '<p class="rule-explanation">' + Ui.esc(item.description) + '</p>' : '';
  }

  function compoundRuleHtml(rule, gi, path, insideVoucher, outerGroup) {
    function select(key, options, current) {
      return '<select data-compound-key="' + key + '" aria-label="' + (key === 'side' ? '判斷範圍' : '符合方式') + '">' + options.map(function (o) {
        return '<option value="' + o[0] + '"' + (current === o[0] ? ' selected' : '') + '>' + o[1] + '</option>';
      }).join('') + '</select>';
    }
    var config = rule.type === 'voucher' ? select('quantifier', [['any','至少一筆符合'],['all','全部符合'],['none','不存在符合']], rule.quantifier) +
      '<p class="rule-field__hint">以下條件由同一筆分錄判斷，再檢查整張傳票。需要限定方向時，加入「借貸別」子條件。</p>' :
      '<p class="rule-field__hint">把下面的條件一起判斷，比對同一筆分錄；用 AND 或 OR 連接。</p>';
    var children = (rule.conditions || []).map(function (child, i) {
      // 子條件之間也是分隔線上的 AND、OR 分段切換（裁定 2026-11-05-16），原本是下拉選單；子條件各自選連接方式，所以每一條分隔線都是切換。
      var join = i ? '<div class="rule-join rule-join--switch rule-join--child" data-motion-key="join-' + motionKey(child, 'rule') + '">' +
          joinToggleHtml('data-child-join="' + i + '"', 'child-join-' + gi + '-' + String(path).replace(/\./g, '-') + '-' + i, child.join === 'OR' ? 'OR' : 'AND', '子條件連接方式') + '</div>' : '';
      return join + ruleRowHtml(child, { matchScope: 'row' }, gi, path + '.' + i, insideVoucher || rule.type === 'voucher');
    }).join('');
    // GL 沒有配對的欄位與前置條件不足的條件照樣列出但不能選，選項後面寫原因。
    var choices = Ui.FilterValues.groupedFields(Store.getState()).map(function (group) {
      return '<optgroup label="' + Ui.esc(group.label) + '">' + group.fields.map(function (f) {
        return '<option value="field:' + Ui.esc(f.id) + '"' + (f.mapped ? '' : ' disabled') + '>' + Ui.esc(f.optionLabel + (f.mapped ? '' : UNMAPPED_OPTION_SUFFIX)) + '</option>';
      }).join('') + '</optgroup>';
    }).join('');
    function typeChoice(type, label) {
      var note = requirementNote([{ type: type }]);
      return '<option value="type:' + type + '"' + (note ? ' disabled' : '') + '>' + Ui.esc(label + (note ? '（' + note + '）' : '')) + '</option>';
    }
    choices += typeChoice('accountSide', '科目分類') + typeChoice('drCrOnly', '借貸別') + typeChoice('manualAuto', '人工/自動分錄') +
      '<option value="type:group">子條件組</option>';
    if (rule.type === 'group' && !insideVoucher) { choices += '<option value="type:voucher">整張傳票條件</option>'; }
    return '<div class="rule-row rule-row--compound' + (FilterStep.selectedRule === rule ? ' rule-row--selected' : '') + '" data-motion-key="' + motionKey(rule, 'rule') + '" data-gi="' + gi + '" data-ri="' + path + '"><div class="filter-rule-heading">' +
      (FilterStep.selectedRule === rule ? '<span class="filter-rule-selected" data-motion="inline">選取中</span>' : '') + '<strong>' +
      (rule.type === 'voucher' ? '整張傳票條件' : '子條件組') + '</strong><button type="button" class="rule-row__remove" data-compound-remove>移除</button></div>' + config + children +
      '<label>加入子條件<select data-child-kind>' + choices + '</select></label><button type="button" class="btn btn--ghost btn--tiny" data-child-add>加入</button></div>';
  }

  // 條件本身缺前置條件時，在卡片上寫後端同一句原因；每一種條件都寫在同一個位置，樣態選單只寫條件的名稱。
  function requirementHintHtml(rule) {
    var note = ownRequirementNote(rule);
    return note ? '<p class="rule-field__hint">' + Ui.esc(note) + '</p>' : '';
  }

  // 樣態條件從哪裡加入：預篩選、常用篩選條件，其餘是自訂條件。常用篩選條件裡只有四張卡帶入樣態條件，
  // 它們各用一個專用的型別，所以看型別就知道來源。
  function patternSource(rule) {
    if (rule.type === 'prescreen') { return 'prescreen'; }
    return isPatternMenuCommonType(rule.type) ? 'common' : 'custom';
  }

  // 常用篩選條件專用型別對應的那張卡；條件列的樣態寫卡片名稱。
  function commonTypeCard(type) {
    return Ui.FILTER_COMMON_CONDITIONS.find(function (card) { return card.kind === 'type' && card.ref === type; }) || null;
  }

  // 每個家族的「對象」下拉：科目看哪一側、風險樣態是哪一種。
  function ruleSubjectHtml(rule, family) {
    var state = Store.getState();
    if (family === 'field' && rule.type !== 'fieldValue') {
      return '';
    }
    if (family === 'account') { return ''; }
    if (family === 'pattern') {
      var selected = rule.type === 'prescreen' ? 'prescreen:' + rule.prescreenKey : rule.type;
      function option(value, label) { return '<option value="' + value + '"' + (value === selected ? ' selected' : '') + '>' + Ui.esc(label) + '</option>'; }
      // 下拉只列同一個來源的條件：從預篩選加入的只列預篩選條件，從常用篩選條件加入的只列常用篩選條件
      // （裁定 2026-10-30-30）。自訂條件只有一種時不放下拉，列標題已經寫了它的名稱。
      var source = patternSource(rule);
      var options = source === 'prescreen'
        ? availablePrescreenKeys().map(function (o) { return { value: 'prescreen:' + o.value, label: o.label }; })
        : source === 'common'
          ? availableCommonTypes().map(function (t) { return { value: t.value, label: (commonTypeCard(t.value) || t).label }; })
          : patternTypeOptions().map(function (t) { return { value: t.value, label: t.label }; });
      var known = options.some(function (item) { return item.value === selected; });
      if (source === 'custom' && known && options.length < 2) { return ''; }
      // 目前這條條件不能用時，選單照樣寫它的名稱，原因寫在卡片上（requirementHintHtml）；類型已停用時選單寫停用的說明。
      var current = rule.type === 'prescreen'
        ? Ui.PRESCREEN_RULES.find(function (o) { return o.value === rule.prescreenKey; })
        : commonTypeCard(rule.type);
      var placeholder = !known ? '<option value="' + Ui.esc(selected) + '" selected disabled>' +
        Ui.esc(current ? current.label : RETIRED_TYPE_NOTE) + '</option>' : '';
      // 左邊的列標題已經寫來源，下拉不再重複一次標題。
      return '<select data-pattern-subject aria-label="樣態">' + placeholder +
        options.map(function (item) { return option(item.value, item.label); }).join('') +
      '</select>';
    }
    return '';
  }

  function bindGroupSelection(container, chooseTargetGroup) {
    container.querySelectorAll('[data-group-index]').forEach(function (well) {
      function choose(event) {
        if (!well.isConnected || event.target.closest('.rule-row') || event.target.closest('[data-group-index]') !== well) { return; }
        chooseTargetGroup(Number(well.dataset.groupIndex), true);
      }
      well.addEventListener('click', choose);
      well.addEventListener('focusin', choose);
      well.addEventListener('keydown', function (event) {
        if (event.target === well && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); choose(event); }
      });
    });
  }

  // 移除一條條件後，焦點交給接替的條件的「移除」：下一條，沒有就上一條；這一層沒有條件了，
  // 交給所在子條件組的「加入子條件」，再沒有就交給所在的那一組；原本焦點會落到整個頁面。
  // 要在移除之前呼叫，才找得到前後的條件。
  function focusAfterRuleRemoval(siblings, index, gi, row) {
    if (!global.JetFocus || !global.JetFocus.deferFirst) { return; }
    var selectors = [siblings[index + 1], siblings[index - 1]].filter(Boolean).map(function (rule) {
      return '.filter-workspace .rule-row[data-motion-key="' + motionKey(rule, 'rule') + '"] > .filter-rule-heading > .rule-row__remove';
    });
    var parentRow = row.parentElement && row.parentElement.closest('.rule-row');
    if (parentRow) {
      selectors.push('.filter-workspace .rule-row[data-motion-key="' + parentRow.getAttribute('data-motion-key') + '"] > label > [data-child-kind]');
    }
    selectors.push('.filter-workspace .scenario-set[data-motion-key="' + groupMotionKey(Store.getState().filter.draft.groups[gi]) + '"]');
    global.JetFocus.deferFirst(selectors);
  }

  function bindCompoundRule(row, siblings, index, gi) {
    var rule = siblings[index];
    function own(selector) { return Array.from(row.querySelectorAll(selector)).filter(function (el) { return el.closest('.rule-row') === row; }); }
    // 括號內加入、移除子條件或移除整個括號算增減條件；其他設定只確認卡片身分並更新自動名稱。
    function changed(structural) {
      var draft = Store.getState().filter.draft;
      refreshAutomaticMetadata(draft, !!structural);
      Store.setFilterDraft(draft);
    }
    own('[data-compound-key]').forEach(function (select) { select.addEventListener('change', function () { rule[select.getAttribute('data-compound-key')] = select.value; changed(); }); });
    own('[data-child-join]').forEach(function (radio) { radio.addEventListener('change', function () { if (!radio.checked) { return; } rule.conditions[Number(radio.getAttribute('data-child-join'))].join = radio.value; changed(); }); });
    own('[data-compound-remove]')[0].addEventListener('click', function () {
      focusAfterRuleRemoval(siblings, index, gi, row);
      siblings.splice(index, 1);
      changed(true);
    });
    own('[data-child-add]')[0].addEventListener('click', function () {
      var value = own('[data-child-kind]')[0].value;
      var fresh;
      if (value.indexOf('type:') === 0) {
        if (requirementNote([{ type: value.slice(5) }])) { return; }
        fresh = Ui.newFilterCondition(value.slice(5));
      } else {
        var f = Ui.FilterValues.fields(Store.getState()).find(function (field) { return field.id === value.slice(6); });
        if (!f || !f.mapped) { return; }
        fresh = Ui.FilterValues.create(f.type);
        delete fresh.field; delete fresh.fieldId;
        fresh[f.extra ? 'fieldId' : 'field'] = f.id;
        if (!f.extra && ['createdBy', 'approvedBy', 'accountNumber'].indexOf(f.id) >= 0) { fresh.operator = 'in'; }
        if (f.extra && f.type === 'amount') { fresh.amountBasis = 'signed'; }
      }
      fresh.join = 'AND'; rule.conditions.push(fresh); changed(true);
    });
  }

  function legacyRulePolicyHtml(rule) {
    var note = '';
    if ((rule.type === 'text' || rule.type === 'textSet') && ['notContains', 'notExact'].indexOf(rule.mode) >= 0) {
      note = '此文字排除沿用原設定：保留空白值。';
    } else if (rule.type === 'typed' && ['notEquals', 'notContains', 'notIn'].indexOf(rule.operator) >= 0) {
      note = '此攸關資料元素欄位條件沿用原設定：不納入空白值。';
    } else if (rule.type === 'accountPair' || rule.type === 'specialAccountCategoryPair') {
      note = '範本分類留白的科目視為 Others；未在配對檔的科目不屬於任何分類。';
    }
    return note ? '<p class="filter-rule-notes rule-field__hint">' + Ui.esc(note) + '</p>' : '';
  }

  // 規則上某一側目前選取的分類身分；只認分類身分陣列，沒有陣列時視為尚未選取。
  function ruleCategoryIds(rule, idsKey) {
    return Array.isArray(rule[idsKey]) ? rule[idsKey] : [];
  }

  function categoryIdsLabel(state, ids) {
    return (ids || []).map(function (id) {
      return Ui.accountCategoryLabel(state, id);
    }).join(Ui.FILTER_CATEGORY_LIST_SEPARATOR);
  }

  // 「選取依據」屬於科目分類的上下層級，第一個正式版隱藏（裁定 2026-11-03-27）；新條件照舊用「包含下層分類」，
  // 分類都是平的一層時，它和「僅分類本身」選到的分類相同。Ui.FEATURES.categoryHierarchy 改成 true 就恢復。
  function categorySelectionHtml(rule) {
    if (!Ui.FEATURES.categoryHierarchy) { return ''; }
    return '<label>選取依據<select data-rule-bind="categorySelection">' + [['role','相同分類用途'],['node','僅分類本身'],['subtree','包含下層分類']].map(function (item) {
      return '<option value="' + item[0] + '"' + ((rule.categorySelection || 'role') === item[0] ? ' selected' : '') + '>' + item[1] + '</option>';
    }).join('') + '</select></label>';
  }

  function categorySelectionModeLabel(rule) {
    return { node: '僅分類本身', subtree: '包含下層分類' }[rule.categorySelection] || '相同分類用途';
  }

  function actualCategoryLabels(rule, key) {
    return Ui.categorySelectionCategories(Store.getState(), ruleCategoryIds(rule, key), rule.categorySelection)
      .map(function (category) { return category.label; }).join(Ui.FILTER_CATEGORY_LIST_SEPARATOR) || '無';
  }

  // 借貸組合讀回會寫出的分類：借方錨定只看借方，貸方錨定只看貸方，其餘兩側都看。
  function pairSelectionIds(rule) {
    var ids = [];
    if (rule.type !== 'accountPair' || rule.pairMode !== 'creditAnchor') { ids = ids.concat(ruleCategoryIds(rule, 'debitCategoryIds')); }
    if (rule.type !== 'accountPair' || rule.pairMode !== 'debitAnchor') { ids = ids.concat(ruleCategoryIds(rule, 'creditCategoryIds')); }
    return ids;
  }

  // 讀回要不要寫選取依據與實際納入分類：選的分類都沒有下層時不寫（裁定 2026-11-03-27，規則在 Ui.categorySelectionMatters）。
  function categorySelectionShown(rule) {
    var ids = rule.type === 'accountSide' ? ruleCategoryIds(rule, 'categoryIds') : pairSelectionIds(rule);
    return Ui.categorySelectionMatters(Store.getState(), ids, rule.categorySelection);
  }

  function pairActualCategories(rule) {
    if (!Store.getState().accountCategory || !categorySelectionShown(rule)) { return ''; }
    var text = '';
    if (rule.type !== 'accountPair' || rule.pairMode !== 'creditAnchor') {
      text += '；借方實際納入分類：' + actualCategoryLabels(rule, 'debitCategoryIds');
    }
    if (rule.type !== 'accountPair' || rule.pairMode !== 'debitAnchor') {
      text += '；貸方實際納入分類：' + actualCategoryLabels(rule, 'creditCategoryIds');
    }
    return text;
  }

  function keywordValues(rule) {
    return Array.from(new Set(Ui.FilterValues.splitDelimited(rule.keywords)));
  }

  function keywordCountText(rule) {
    return '已輸入 ' + keywordValues(rule).length + ' 個值';
  }

  function keywordCountHtml(rule) {
    return '<p class="rule-field__hint" data-keyword-count role="status">' + keywordCountText(rule) + '</p>';
  }

  function ruleControlsHtml(rule, gi, ri, sameRowChild) {
    return requirementHintHtml(rule) + ruleTypeControlsHtml(rule, gi, ri, sameRowChild);
  }

  function ruleTypeControlsHtml(rule, gi, ri, sameRowChild) {
    // 舊式條件的欄位下拉：GL 沒有配對的欄位不能選；已選的欄位保留原名，原因寫在卡片上。
    function fieldSelect(keys) {
      return '<select data-rule-bind="field">' + keys.map(function (key) {
        var mapped = Ui.glFieldAvailable(Store.getState(), key), chosen = rule.field === key;
        return '<option value="' + key + '"' + (chosen ? ' selected' : '') + (mapped ? '' : ' disabled') + '>' +
          Ui.esc(Ui.glFieldLabel(key) + (mapped || chosen ? '' : UNMAPPED_OPTION_SUFFIX)) + '</option>';
      }).join('') + '</select>';
    }

    // 分類多選：每一側送出科目分類的分類身分陣列。同一 fieldset 內以 checkbox 呈現，
    // 去重與順序無關（選兩個同類型與只選其中一個等價，判定只看類型）。
    function categoryMultiSelect(idsKey, legend) {
      var state = Store.getState();
      var selected = ruleCategoryIds(rule, idsKey);
      var view = categoryPickerView(rule, idsKey);
      view.focusKey = 'cat-' + gi + '-' + ri + '-' + idsKey;
      return Ui.categoryPickerHtml(state, selected, idsKey, legend, view);
    }

    switch (rule.type) {
      case 'fieldValue':
        return Ui.FilterValues.render(rule, Store.getState(), 'value-' + gi + '-' + ri);
      case 'accountSide':
        return categorySelectionHtml(rule) + '<select data-rule-bind="categoryMode" aria-label="科目條件">' +
          Ui.ACCOUNT_SIDE_MODE_OPTIONS.map(function (item) {
            return '<option value="' + item.value + '"' + (rule.categoryMode === item.value ? ' selected' : '') + '>' + item.label + '</option>';
          }).join('') + '</select>' +
          categoryMultiSelect('categoryIds', '指定分類') +
          '<p class="rule-field__hint">此條件只判斷同一筆分錄的分類。範本分類留白的科目視為 Others；未在配對檔的科目不屬於任何分類。</p>';
      case 'prescreen': {
        var explanation = Ui.prescreenConditionDescription(rule.prescreenKey);
        var explanationHtml = explanation ? '<p class="rule-field__hint">' + Ui.esc(explanation) + '</p>' : '';
        // 缺期末財報準備日等前置條件時，原因寫在樣態選單上；有準備日時這裡寫出比較的日期。
        if (rule.prescreenKey === 'prepPeriodApproval' && (Store.getState().project || {}).prepStartDate) {
          explanationHtml += '<p class="rule-field__hint">' + Ui.esc(ruleSummaryLabel(rule, 0)) + '</p>';
        }
        return explanationHtml;
      }

      case 'text':
        return fieldSelect(Ui.FILTER_TEXT_FIELDS) +
          '<textarea rows="1" data-rule-bind="keywords" placeholder="輸入關鍵字">' + Ui.esc(rule.keywords) + '</textarea>' +
          '<span class="rule-field__hint">可用換行、半形或全形逗號、頓號與 Tab 分隔。</span>' +
          '<select data-rule-bind="mode">' + Ui.TEXT_MODE_OPTIONS.map(function (o) {
            return '<option value="' + o.value + '"' + (rule.mode === o.value ? ' selected' : '') + '>' +
              o.label + '</option>';
          }).join('') + '</select>' + keywordCountHtml(rule);

      case 'textSet': {
        var values = Array.isArray(rule.values) ? rule.values : [];
        var helpId = 'text-set-values-help-' + gi + '-' + ri;
        return fieldSelect(Ui.FILTER_TEXT_FIELDS) +
          '<span class="rule-field rule-field--values">' +
            '<textarea rows="3" data-rule-bind="values" aria-describedby="' + helpId + '">' +
              Ui.esc(values.join('\n')) + '</textarea>' +
            '<span class="rule-field__hint" id="' + helpId + '">每行一個值（最多 ' +
              Ui.TEXT_SET_MAX_VALUES + ' 個）。值內的逗號、頓號與 Tab 保留，不會拆開。</span>' +
          '</span>' +
          '<select data-rule-bind="mode">' + Ui.TEXT_SET_MODE_OPTIONS.map(function (o) {
            return '<option value="' + o.value + '"' + (rule.mode === o.value ? ' selected' : '') + '>' +
              o.label + '</option>';
          }).join('') + '</select>' +
          '<select data-rule-bind="normalization">' + Ui.TEXT_SET_NORMALIZATION_OPTIONS.map(function (o) {
            return '<option value="' + o.value + '"' + (rule.normalization === o.value ? ' selected' : '') + '>' +
              o.label + '</option>';
          }).join('') + '</select>';
      }

      case 'dateRange':
        return fieldSelect(Ui.FILTER_DATE_FIELDS) +
          Ui.dateInputHtml({ 'data-rule-bind': 'from', value: rule.from, 'aria-label': '起日' }) +
          '<span class="rule-row__sep">～</span>' +
          Ui.dateInputHtml({ 'data-rule-bind': 'to', value: rule.to, 'aria-label': '迄日' });

      case 'numRange':
        return '<span class="rule-row__field-label" title="不分借貸，以金額大小比較（取絕對值）">金額（絕對值）</span>' +
          '<input type="number" data-rule-bind="from" placeholder="最小金額" value="' + Ui.esc(rule.from) + '">' +
          '<span class="rule-row__sep">～</span>' +
          '<input type="number" data-rule-bind="to" placeholder="最大金額" value="' + Ui.esc(rule.to) + '">';

      case 'drCrOnly':
        return '<select data-rule-bind="drCr">' +
          '<option value="debit"' + (rule.drCr !== 'credit' ? ' selected' : '') + '>僅借方</option>' +
          '<option value="credit"' + (rule.drCr === 'credit' ? ' selected' : '') + '>僅貸方</option>' +
        '</select>';

      case 'manualAuto':
        return '<select data-rule-bind="isManual">' +
          '<option value="true"' + (rule.isManual !== false && rule.isManual !== 'false' ? ' selected' : '') + '>人工分錄</option>' +
          '<option value="false"' + (rule.isManual === false || rule.isManual === 'false' ? ' selected' : '') + '>自動分錄</option>' +
        '</select>';

      case 'accountPair':
      case 'specialAccountCategoryPair': {
        // 借貸科目組合：一張卡、五句白話模式，跨兩個 wire 型別（ACCOUNT_COMBINATION_OPTIONS）。
        // 選到另一個型別的模式時由 bind 重建規則並保留兩側分類；錨定模式只顯示用到的那一側。
        var current = Ui.ACCOUNT_COMBINATION_OPTIONS.filter(function (o) { return o.type === rule.type && o.mode === rule.pairMode; })[0];
        var modeLabels = { drAndCr: '指定借方與貸方', drNotCr: '指定借方，排除貸方分類', notDrCr: '指定貸方，排除借方分類',
          debitAnchor: '指定借方，查看對方科目', creditAnchor: '指定貸方，查看對方科目', exact: '指定借方與貸方（舊格式）' };
        var pairSelect = '<div class="filter-account-pair"><label class="filter-pair-mode">查找方式<select data-pair-selection>' + Ui.ACCOUNT_COMBINATION_OPTIONS.filter(function (o) {
            return !o.legacy || (current && current.legacy);
          }).map(function (o) {
            var selected = current && o.type === current.type && o.mode === current.mode;
            return '<option value="' + o.type + '|' + o.mode + '"' + (selected ? ' selected' : '') + '>' + Ui.esc(modeLabels[o.mode] || o.label) + '</option>';
          }).join('') + '</select></label>';
        var showDebit = rule.type !== 'accountPair' || rule.pairMode !== 'creditAnchor';
        var showCredit = rule.type !== 'accountPair' || rule.pairMode !== 'debitAnchor';
        return pairSelect +
          (showDebit ? categoryMultiSelect('debitCategoryIds', rule.pairMode === 'notDrCr' ? '整張傳票不得有這些借方分類' : '借方分類') : '') +
          (showCredit ? categoryMultiSelect('creditCategoryIds', rule.pairMode === 'drNotCr' ? '整張傳票不得有這些貸方分類' : '貸方分類') : '') +
          '<details class="filter-rule-help" data-rule-help' + disclosureAttributes('rule-help-' + motionKey(rule, 'rule')) + '><summary>查找說明</summary><p class="filter-pair-result">' + (rule.pairMode === 'drNotCr' ? '結果列出符合的借方分錄。' : rule.pairMode === 'notDrCr' ? '結果列出符合的貸方分錄。' :
            rule.pairMode === 'debitAnchor' ? '結果列出符合的借方分錄及同傳票全部貸方分錄。' : rule.pairMode === 'creditAnchor' ? '結果列出符合的貸方分錄及同傳票全部借方分錄。' : '結果列出同傳票符合分類的借方與貸方分錄。') + '</p>' + commonDescriptionHtml(rule) + legacyRulePolicyHtml(rule) + '</details></div>';
      }

      case 'typed': {
        // 攸關資料元素條件：欄位、比較方式與輸入形狀都跟著已提交的欄位型別走；
        // 前端不猜型別、不換算金額，operand 一律原樣送出由系統端解析。
        var fields = rdeFieldOptions();
        var valueType = rdeFieldValueType(rule.fieldId);
        var operators = Ui.typedOperatorsForValueType(valueType);
        var carrier = Ui.typedOperatorCarrier(rule.operator);
        var typedHelpId = 'typed-values-help-' + gi + '-' + ri;

        var fieldOptions = '<option value="">（請選擇欄位）</option>' + fields.map(function (f) {
          return '<option value="' + Ui.esc(f.id) + '"' +
            (rule.fieldId === f.id ? ' selected' : '') + '>' + Ui.esc(f.optionLabel) + '</option>';
        }).join('');
        var typedHtml = '<select data-rule-bind="fieldId">' + fieldOptions + '</select>';

        if (!valueType) {
          // 原本選的欄位已不在欄位配對中，和後端同一句（裁定 2026-10-14-01）。
          return typedHtml + '<span class="rule-row__field-label">' + (rule.fieldId
            ? '所用的攸關資料元素欄位已不在欄位配對中，請重新選擇欄位或刪除此條件' : '請先選擇欄位') + '</span>';
        }

        typedHtml += '<select data-rule-bind="operator">' +
          '<option value="">（請選擇比較方式）</option>' +
          operators.map(function (o) {
            return '<option value="' + o.value + '"' + (rule.operator === o.value ? ' selected' : '') + '>' +
              o.label + '</option>';
          }).join('') + '</select>';

        var inputType = valueType === 'date' ? 'date' : (valueType === 'amount' ? 'number' : 'text');
        function typedInput(key) {
          return inputType === 'date' ? Ui.dateInputHtml({ 'data-rule-bind': key, value: rule[key], 'aria-label': key === 'from' ? '起日' : key === 'to' ? '迄日' : '日期' }) :
            '<input type="' + inputType + '" data-rule-bind="' + key + '" value="' + Ui.esc(rule[key]) + '">';
        }
        if (carrier === 'value' && rule.operator) {
          typedHtml += typedInput('value');
        } else if (carrier === 'range') {
          typedHtml += typedInput('from') +
            '<span class="rule-row__sep">～</span>' +
            typedInput('to');
        } else if (carrier === 'set') {
          var typedValues = Array.isArray(rule.values) ? rule.values : [];
          typedHtml += '<span class="rule-field rule-field--values">' +
            '<textarea rows="3" data-rule-bind="values" aria-describedby="' + typedHelpId + '">' +
              Ui.esc(typedValues.join('\n')) + '</textarea>' +
            '<span class="rule-field__hint" id="' + typedHelpId + '">每行一個值（最多 ' +
              Ui.TYPED_SET_MAX_VALUES + ' 個）</span>' +
          '</span>';
        }

        if (valueType === 'amount' && carrier !== 'none') {
          typedHtml += '<select data-rule-bind="amountBasis">' +
            Ui.TYPED_AMOUNT_BASIS_OPTIONS.map(function (o) {
              return '<option value="' + o.value + '"' +
                (rule.amountBasis === o.value ? ' selected' : '') + '>' + o.label + '</option>';
            }).join('') + '</select>';
        }

        return typedHtml;
      }

      case 'entityFrequency': {
        function countSelect(key, label, items) {
          return '<label>' + label + '<select data-rule-bind="' + key + '">' + items.map(function (item) {
            return '<option value="' + item[0] + '"' + (rule[key] === item[0] ? ' selected' : '') + '>' + Ui.esc(item[1]) + '</option>';
          }).join('') + '</select></label>';
        }
        return countSelect('field', '統計對象', ['accountNumber', 'createdBy', 'approvedBy'].map(function (key) { return [key, Ui.glFieldLabel(key)]; })) +
          countSelect('countUnit', '計算單位', [['entries', '分錄筆數'], ['vouchers', '傳票張數（同號只算一張）']]) +
          countSelect('countOperator', '比較方式', [['equals', '等於'], ['lessThan', '小於'], ['greaterThan', '大於'], ['between', '介於'],
            ['lessThanOrEqual', '小於或等於'], ['greaterThanOrEqual', '大於或等於']]) +
          '<input type="number" data-rule-bind="countFrom" min="0" step="1" aria-label="統計次數或區間下限" value="' + Ui.esc(rule.countFrom) + '">' +
          (rule.countOperator === 'between' ? '<span>至</span><input type="number" data-rule-bind="countTo" min="0" step="1" aria-label="統計區間上限" value="' + Ui.esc(rule.countTo) + '"><span>含兩個端點</span>' : '') +
          '<p class="rule-explanation">在 GL 中依匯入後的科目或人員分組；空白欄位不列入。' +
          (rule.countUnit === 'vouchers' ? '同案件相同傳票號碼算一張，空白號碼不計張；符合該科目或人員的分錄仍會列出。' : '同張傳票有多筆分錄時，每筆都計入。') + '</p>';
      }

      case 'revenueDebitNearQuarterEnd':
        return '<span class="rule-row__field-label" title="總帳日期落在曆年季底或查核期末前指定天數內的收入借方分錄">季底或查核期末前</span>' +
          '<input type="number" data-rule-bind="windowDays" min="1" max="92" step="1" placeholder="天數" value="' +
            Ui.esc(rule.windowDays) + '">' +
          '<span class="rule-row__sep">天內借記收入</span>';

      case 'revenueWithoutNormalCounterpart':
        return '<span class="rule-row__field-label" title="貸方為收入，但同一張傳票沒有應收或預收的借方分錄">貸方為收入但借方非一般對方科目</span>';

      case 'manualRevenueEntry':
        return '<span class="rule-row__field-label" title="科目為收入且為人工分錄">收入的人工分錄</span>';

      case 'preparerEqualsApprover':
        return '<span class="rule-row__field-label" title="同一張傳票的建立人員與核准人員相同">編製人員與核准人員相同</span>';

      default:
        return '';
    }
  }

  function isExplicitVoucherCondition(rule) {
    return rule.type === 'voucher' || rule.type === 'group' && (rule.conditions || []).length > 0 && rule.conditions.every(isExplicitVoucherCondition);
  }

  function containsExplicitVoucherCondition(rule) {
    return rule.type === 'voucher' || (rule.conditions || []).some(containsExplicitVoucherCondition);
  }

  // 僅供日期讀回，實際命中仍由後端 QuarterEndWindows 計算。
  function quarterEndSummary(rule) {
    var text = '季底或查核期末前 ' + (rule.windowDays || '…') + ' 天借記收入';
    var project = Store.getState().project || {}, days = Number(rule.windowDays);
    if (!project.periodStart || !project.periodEnd || days < 1 || days > 92) { return text; }
    var start = new Date(project.periodStart + 'T00:00:00Z'), end = new Date(project.periodEnd + 'T00:00:00Z');
    if (!Number.isFinite(start.getTime()) || !Number.isFinite(end.getTime()) || start > end) { return text; }
    var windows = [], day = 86400000;
    for (var year = start.getUTCFullYear(); year <= end.getUTCFullYear(); year++) {
      [3, 6, 9, 12].forEach(function (month) {
        var q = Date.UTC(year, month, 0), from = q - (days - 1) * day;
        if (q >= start.getTime() && from <= end.getTime()) { windows.push([from, q]); }
      });
    }
    windows.push([end.getTime() - (days - 1) * day, end.getTime()]);
    windows.sort(function (a, b) { return a[0] - b[0]; });
    var merged = [];
    windows.forEach(function (w) {
      var last = merged[merged.length - 1];
      if (last && w[0] <= last[1] + day) { last[1] = Math.max(last[1], w[1]); } else { merged.push(w); }
    });
    return text + '；日期區間：' + merged.map(function (w) { return new Date(w[0]).toISOString().slice(0, 10) + '～' + new Date(w[1]).toISOString().slice(0, 10); }).join('、');
  }

  function ruleSummaryLabel(rule, index, compact) {
    var prefix = index === 0 ? '' : (effectiveConditionJoin(rule) === 'OR' ? '或 ' : '且 ');
    if ((rule.type === 'accountPair' || rule.type === 'specialAccountCategoryPair') && categorySelectionShown(rule)) {
      prefix += categorySelectionModeLabel(rule) + '：';
    }
    switch (rule.type) {
      case 'group':
      case 'voucher': {
        if (isNonBusinessDayBracket(rule)) { return prefix + Ui.FILTER_COMMON_ATOM_LABELS.nonBusinessDay; }
        if (isLegacyWeekendHolidayBracket(rule)) { return prefix + '（週末過帳 或 假日過帳）'; }
        var expr = '';
        (rule.conditions || []).forEach(function (child) {
          var atom = ruleSummaryLabel(child, 0, compact);
          expr = expr ? '（' + expr + (effectiveConditionJoin(child) === 'OR' ? ' 或 ' : ' 且 ') + atom + '）' : atom;
        });
        var head = rule.type === 'voucher' ? '整張傳票' +
          ({ any: '至少一筆符合', all: '全部符合（至少有一筆）', none: '不存在符合' }[rule.quantifier] || '') + '：' : '';
        if (rule.type === 'voucher') { return prefix + head + (rule.conditions.length === 1 ? expr : '同一分錄（' + expr + '）'); }
        var scope = rule.conditions.length > 0 && rule.conditions.every(isExplicitVoucherCondition) ? '同張傳票'
          : rule.conditions.some(containsExplicitVoucherCondition) ? '條件組合' : '同一分錄';
        return prefix + scope + '（' + expr + '）';
      }
      case 'fieldValue':
        return prefix + Ui.FilterValues.summary(rule, Store.getState(), compact);
      case 'accountSide': {
        var categories = categoryIdsLabel(Store.getState(), rule.categoryIds);
        var text = '尚未選擇分類條件';
        if (rule.categoryMode === 'is') { text = '科目屬於「' + categories + '」'; }
        if (rule.categoryMode === 'isNot') { text = '科目不屬於「' + categories + '」'; }
        if (!categorySelectionShown(rule)) { return prefix + text; }
        return prefix + text + '（' + categorySelectionModeLabel(rule) + '）' +
          (Store.getState().accountCategory ? '；實際納入分類：' + actualCategoryLabels(rule, 'categoryIds') : '');
      }
      case 'prescreen': {
        var hit = Ui.PRESCREEN_RULES.filter(function (o) { return o.value === rule.prescreenKey; })[0];
        var preparationDate = (Store.getState().project || {}).prepStartDate;
        return prefix + '預篩選：' + (hit ? hit.label : Ui.RETIRED_CONDITION_LABEL) +
          (rule.prescreenKey === 'prepPeriodApproval' && preparationDate ? '（' + preparationDate + ' 起，含當日）' : '');
      }
      case 'text': {
        // 裁定 2026-11-03-01：關鍵字照送出時的寫法拆開、去掉重複再用逗號接回，讀回和存下的條件一致；後端 FilterConditionRenderer 同一規則。
        var mode = Ui.TEXT_MODE_OPTIONS.filter(function (o) { return o.value === rule.mode; })[0];
        return prefix + Ui.glFieldReadback(Store.getState(), rule.field) + ' ' + (mode ? mode.label : Ui.RETIRED_CONDITION_LABEL) +
          '「' + keywordValues(rule).join(',') + '」';
      }
      case 'textSet': {
        var textSetMode = Ui.TEXT_SET_MODE_OPTIONS.filter(function (o) { return o.value === rule.mode; })[0];
        var textSetNormalization = Ui.TEXT_SET_NORMALIZATION_OPTIONS.filter(function (o) {
          return o.value === rule.normalization;
        })[0];
        var textSetValues = Array.isArray(rule.values) ? rule.values : [];
        return prefix + Ui.glFieldReadback(Store.getState(), rule.field) + ' ' +
          (textSetMode ? textSetMode.label : Ui.TEXT_SET_MODE_OPTIONS[0].label) +
          '「' + textSetValues.join('、') + '」（' +
          (textSetNormalization ? textSetNormalization.label : Ui.TEXT_SET_NORMALIZATION_OPTIONS[0].label) + '）';
      }
      case 'dateRange':
        return prefix + Ui.glFieldReadback(Store.getState(), rule.field) + ' ' + (rule.from || '…') + '～' + (rule.to || '…');
      case 'numRange':
        return prefix + '金額（絕對值） ' + (rule.from ? '≥ ' + rule.from : '') +
          (rule.from && rule.to ? '、' : '') + (rule.to ? '≤ ' + rule.to : '');
      case 'drCrOnly':
        return prefix + (rule.drCr === 'credit' ? '僅貸方' : '僅借方');
      case 'manualAuto':
        return prefix + (rule.isManual === false || rule.isManual === 'false' ? '自動分錄' : '人工分錄');
      case 'accountPair': {
        var pairMode = Ui.ACCOUNT_PAIR_MODE_OPTIONS.filter(function (o) { return o.value === rule.pairMode; })[0];
        var pairState = Store.getState();
        var pairDebit = categoryIdsLabel(pairState, ruleCategoryIds(rule, 'debitCategoryIds'));
        var pairCredit = categoryIdsLabel(pairState, ruleCategoryIds(rule, 'creditCategoryIds'));
        var pairDetail = rule.pairMode === 'debitAnchor'
          ? '借方 ' + pairDebit
          : (rule.pairMode === 'creditAnchor'
            ? '貸方 ' + pairCredit
            : '借方 ' + pairDebit + '，貸方 ' + pairCredit);
        return prefix + '借貸科目組合：' + (pairMode ? pairMode.label : Ui.RETIRED_CONDITION_LABEL) + '（' + pairDetail + '）' + pairActualCategories(rule);
      }
      case 'specialAccountCategoryPair': {
        var specialMode = Ui.SPECIAL_PAIR_MODE_OPTIONS.filter(function (o) { return o.value === rule.pairMode; })[0];
        var specialState = Store.getState();
        return prefix + '借貸科目組合：' + (specialMode ? specialMode.label : Ui.RETIRED_CONDITION_LABEL) + '（借方 ' +
          categoryIdsLabel(specialState, ruleCategoryIds(rule, 'debitCategoryIds')) +
          '，貸方 ' +
          categoryIdsLabel(specialState, ruleCategoryIds(rule, 'creditCategoryIds')) + '）' + pairActualCategories(rule);
      }
      case 'typed': {
        // 讀回逐字對齊系統端 renderer：欄位以目前顯示名稱呈現；欄位已不在配對中時寫「已不在欄位配對中的攸關資料元素欄位」，
        // 不接比較基準（裁定 2026-10-14-01）。operand 依 carrier 呈現原始輸入值，金額條件附比較基準。
        var typedLabel = Ui.rdeFieldLabel(Store.getState(), rule.fieldId);
        var typedOp = Ui.typedOperatorLabel(rule.operator);
        var typedCarrier = Ui.typedOperatorCarrier(rule.operator);
        var typedOperand = '';
        if (typedCarrier === 'range') {
          typedOperand = '「' + (rule.from || '…') + '」～「' + (rule.to || '…') + '」';
        } else if (typedCarrier === 'set') {
          typedOperand = '「' + (Array.isArray(rule.values) ? rule.values : [])
            .join(Ui.FILTER_CATEGORY_LIST_SEPARATOR) + '」';
        } else if (typedCarrier === 'value') {
          typedOperand = '「' + (rule.value || '') + '」';
        }
        var typedBasis = '';
        if (rdeFieldValueType(rule.fieldId) === 'amount' && typedCarrier !== 'none') {
          var basis = Ui.TYPED_AMOUNT_BASIS_OPTIONS.filter(function (o) {
            return o.value === rule.amountBasis;
          })[0];
          typedBasis = basis ? '（' + basis.label + '）' : '';
        }
        return prefix + typedLabel + ' ' + typedOp + typedOperand + typedBasis;
      }
      case 'entityFrequency':
        return prefix + 'GL 中「' + Ui.glFieldLabel(rule.field) + '」' +
          (rule.countUnit === 'vouchers' ? '傳票張數（同號只算一張）' : '分錄筆數') + ' ' +
          ({ equals: '等於', lessThan: '小於', greaterThan: '大於', between: '介於', lessThanOrEqual: '小於或等於', greaterThanOrEqual: '大於或等於' }[rule.countOperator] || '') +
          ' ' + rule.countFrom + (rule.countOperator === 'between' ? '～' + rule.countTo + '（含端點）' : '');
      case 'revenueDebitNearQuarterEnd':
        return prefix + quarterEndSummary(rule);
      case 'revenueWithoutNormalCounterpart':
        return prefix + '貸方為收入但借方非一般對方科目';
      case 'manualRevenueEntry':
        return prefix + '收入之人工分錄';
      case 'preparerEqualsApprover':
        return prefix + '編製人員與核准人員相同';
      default:
        return prefix + Ui.RETIRED_CONDITION_LABEL;
    }
  }

  // 多組時，條件總覽標題同一列寫「加入到第 1、2、3 組」（裁定 2026-11-05-16）：組號是和 AND、OR 同一種分段切換，按一下就換加入的組，
  // 右側選取的組跟著換；點右側的組時這裡也跟著換。只留這一套組別選擇（裁定 2026-09-09-08），原本是獨占一列的下拉選單。
  // 「加入到第」與「組」只是畫面上的字，螢幕報讀讀單選鈕群組的「加入到」與每一格的「第 N 組」。
  function conditionTargetHtml(draft) {
    var groups = draft.groups.map(function (group, index) { return { group: group, index: index }; });
    if (groups.length < 2) { return ''; }
    var active = activeEditableGroup(draft);
    return '<div class="filter-target" data-motion-key="target" data-motion="inline">' +
      '<span class="filter-target__lead" aria-hidden="true">加入到第</span>' +
      '<span class="join-toggle filter-target__groups" role="radiogroup" aria-label="加入到">' + groups.map(function (item, position) {
        return '<label class="join-toggle__option"><input type="radio" name="filter-condition-target" value="' + item.index + '" data-condition-target' +
          ' aria-label="第 ' + (position + 1) + ' 組"' + (item.group === active ? ' checked' : '') + '><span>' + (position + 1) + '</span></label>';
      }).join('') + '</span>' +
      '<span class="filter-target__tail" aria-hidden="true">組</span></div>';
  }

  // 會改變同一列可見控制項組合的規則鍵：改動後必須重繪，否則畫面會停在舊的輸入形狀。
  var STRUCTURAL_RULE_KEYS = ['fieldId', 'operator', 'pairMode', 'categoryMode', 'countUnit', 'countOperator'];

  // 條件組與條件列：加入目標、組的增刪與連接方式、每一列的控制項（原本 bind 裡的兩段）。
  function bindConditionRows(container) {
    var targetOptions = container.querySelectorAll('[data-condition-target]');
    targetOptions.forEach(function (radio) {
      radio.addEventListener('change', function () { if (radio.checked) { chooseTargetGroup(Number(radio.value), true); } });
    });
    bindGroupSelection(container, chooseTargetGroup);
    // 只同步加入目標及其呈現，不重建正在輸入的條件，也不移動焦點或捲軸。
    function chooseTargetGroup(gi, clearSelection) {
      var draft = Store.getState().filter.draft;
      setActiveGroup(draft, draft.groups[gi]);
      if (clearSelection) {
        FilterStep.selectedRule = null;
        container.querySelectorAll('.rule-row--selected').forEach(function (row) { row.classList.remove('rule-row--selected'); });
        container.querySelectorAll('.filter-rule-selected:not([data-motion-folding])').forEach(dropRuleBadge);
      }
      // 「加入到第 N 組」換一格；滑動的選取在下面換組框的 class 時跟著重新定位。
      targetOptions.forEach(function (radio) { radio.checked = Number(radio.value) === gi; });
      container.querySelectorAll('[data-group-index]').forEach(function (well) {
        var active = Number(well.dataset.groupIndex) === gi;
        well.classList.toggle('scenario-set--active', active);
        well.setAttribute('aria-current', String(active));
        well.querySelector('.filter-group-selected').hidden = !active;
      });
      // 條件總覽三個分頁的「加入」與「已加入」跟著換成這一組的狀態，只換掉狀態變了的列。
      refreshConditionKeys(container, draft);
    }
    // 「＋ 另一組條件」：新增一個可編輯群組（一個 set）。新組 join 繼承有效組間運算子——畫面上只有
    // 一個情境層運算子，硬編碼 'OR' 會在使用者已改 AND 時打破「全組一致」不變量（段控顯示 AND、
    // 新組實際帶 OR 送出）。從一組加到第二組時尚無組間運算子，scenarioJoin 回退預設 OR，
    // 與段控首次出現時的顯示一致。只在已有條件時才出現（見 scenarioBuilderHtml），故綁定前判空。
    var addSetBtn = container.querySelector('[data-action="add-set"]');
    if (addSetBtn) {
      addSetBtn.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        // 把編輯器上已顯示的空白第一組實際建立到草稿裡。
        if (draft.groups.length === 0) {
          var first = { join: 'AND', matchScope: 'row', conditions: [] };
          keepMotionKey(EMPTY_FIRST_GROUP, first);
          draft.groups.unshift(first);
        }
        var fresh = { join: scenarioJoin(draft), matchScope: 'row', conditions: [] };
        draft.groups.push(fresh);
        setActiveGroup(draft, fresh); // 新組即新落點：設為作用中
        FilterStep.selectedRule = null;
        Store.setFilterDraft(draft);
        // 新組已成為條件總覽「加入到」的組；在右側直接呈現它，焦點不回到頁首。
        if (global.JetFocus) { global.JetFocus.defer(function () {
          var workbench = document.querySelector('.filter-workbench');
          if (workbench && global.getComputedStyle(workbench).gridTemplateColumns.split(' ').length === 1) {
            var chosen = Array.prototype.filter.call(document.querySelectorAll('.filter-workspace [data-condition-target]'), function (radio) { return radio.checked; })[0];
            return chosen || document.querySelector('.filter-workspace [data-condition-source]');
          }
          return document.querySelector('.filter-workspace [data-group-index="' + draft.groups.indexOf(fresh) + '"]');
        }); }
      });
    }

    // 清空只影響這一組草稿；保留組間關係、其他組及使用者手寫的情境資訊。
    container.querySelectorAll('[data-action="clear-set"]').forEach(function (button) {
      button.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var gi = Number(button.getAttribute('data-gi'));
        var group = draft.groups[gi];
        if (!group || !group.conditions.length) { return; }
        var fresh = { join: group.join, matchScope: 'row', conditions: [] };
        keepMotionKey(group, fresh);
        draft.groups[gi] = fresh;
        setActiveGroup(draft, fresh);
        FilterStep.selectedRule = null;
        refreshAutomaticMetadata(draft, true);
        Store.setFilterDraft(draft);
        // 清除後這一組的「清除」不能按，焦點交給這一組。
        if (global.JetFocus && global.JetFocus.deferFirst) {
          global.JetFocus.deferFirst(['.filter-workspace .scenario-set[data-motion-key="' + groupMotionKey(fresh) + '"]']);
        }
      });
    });

    // 「移除這組」：splice 該可編輯群組（一個 set）。移到只剩一組會自動回乾淨單 well（無模式）。
    // 移除作用中組時重新標記作用中（回退到最後一組），確保恆有一個有效落點。
    container.querySelectorAll('[data-action="remove-set"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var gi = Number(btn.getAttribute('data-gi'));
        // 焦點交給下一組，沒有就上一組，都沒有時交給「新增條件組」。
        var successors = [draft.groups[gi + 1], draft.groups[gi - 1]].filter(Boolean).map(function (group) {
          return '.filter-workspace .scenario-set[data-motion-key="' + groupMotionKey(group) + '"]';
        }).concat(['.filter-workspace [data-action="add-set"]']);
        draft.groups.splice(gi, 1);
        setActiveGroup(draft, activeEditableGroup(draft));
        refreshAutomaticMetadata(draft, true);
        Store.setFilterDraft(draft);
        if (global.JetFocus && global.JetFocus.deferFirst) { global.JetFocus.deferFirst(successors); }
      });
    });

    container.querySelectorAll('[data-set-combinator], [data-set-join]').forEach(function (control) {
      var kind = control.hasAttribute('data-set-join') ? 'join' : 'combinator';
      var groupIndex = control.hasAttribute('data-set-join') ? control.dataset.setJoinIndex : control.dataset.gi;
      control.setAttribute('data-focus-key', 'filter-group-' + groupIndex + '-' + kind + '-' + control.value);
    });

    // 條件組組合器（組內的 AND、OR 分段切換，每組一個）：設「該群組(data-gi)」各 rule 的 join。
    // I 的子條件組和其他條件一樣跟著該組設定；括號內的「週末或假日」由子條件自己的 join 決定，不受影響。
    container.querySelectorAll('[data-set-combinator]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        if (!radio.checked) { return; }
        var value = radio.value;
        var draft = Store.getState().filter.draft;
        var group = draft.groups[Number(radio.getAttribute('data-gi'))];
        if (!group) { return; }
        group.conditions.forEach(function (r) { r.join = value; });
        Store.setFilterDraft(draft);
      });
    });

    // 條件組之間的連接器（AND、OR 分段切換）：單一組間運算子——把所有群組的 join 設為一致值；
    // 重開的已儲存情境（__preserveJoins）只改這一個連接器對應的組。
    container.querySelectorAll('[data-set-join]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        if (!radio.checked) { return; }
        var value = radio.value;
        var draft = Store.getState().filter.draft;
        if (draft.__preserveJoins) { draft.groups[Number(radio.dataset.setJoinIndex)].join = value; }
        else { draft.groups.forEach(function (g) { g.join = value; }); }
        Store.setFilterDraft(draft);
      });
    });

    container.querySelectorAll('.rule-row').forEach(function (row) {
      var gi = Number(row.getAttribute('data-gi'));
      var path = row.getAttribute('data-ri').split('.').map(Number);
      var ri = path.pop();
      var currentRules = Store.getState().filter.draft.groups[gi].conditions;
      path.forEach(function (index) { currentRules = currentRules[index].conditions; });
      function patchCurrent(patch) {
        Object.assign(currentRules[ri], patch);
        Store.patchFilterCondition(gi, path.length ? path[0] : ri, {});
      }
      var currentRule = currentRules[ri];
      function selectCurrentRule(event) {
        if (!row.isConnected || event.target.closest('.rule-row') !== row || FilterStep.selectedRule === currentRule) { return; }
        var draft = Store.getState().filter.draft;
        if (activeEditableGroup(draft) !== draft.groups[gi]) { chooseTargetGroup(gi, false); }
        FilterStep.selectedRule = currentRule;
        container.querySelectorAll('.rule-row--selected').forEach(function (item) { item.classList.remove('rule-row--selected'); });
        container.querySelectorAll('.filter-rule-selected:not([data-motion-folding])').forEach(dropRuleBadge);
        row.classList.add('rule-row--selected');
        var badge = document.createElement('span');
        badge.className = 'filter-rule-selected'; badge.textContent = '選取中';
        badge.setAttribute('data-motion', 'inline');
        row.querySelector('.filter-rule-heading').prepend(badge);
        // 「選取中」從左邊打開、換到別條時收回去（裁定 2026-10-13-11：出現與消失都要有動態）。
        if (global.JetMotion && typeof global.JetMotion.enter === 'function') { global.JetMotion.enter(badge); }
      }
      row.addEventListener('click', selectCurrentRule);
      row.addEventListener('focusin', selectCurrentRule);
      if (currentRule.type === 'group' || currentRule.type === 'voucher') {
        bindCompoundRule(row, currentRules, ri, gi);
        return;
      }
      // 沿用 app.js 的明確焦點鍵；重繪篩選方式或訊號選單時留在原控制項。
      ['data-pattern-subject', 'data-account-subject', 'data-value-kind', 'data-value-polarity', 'data-rule-bind', 'data-value-key'].forEach(function (attribute) {
        row.querySelectorAll('[' + attribute + ']').forEach(function (control) {
          if (!control.hasAttribute('data-focus-key')) {
            control.setAttribute('data-focus-key', 'filter-' + gi + '-' + row.getAttribute('data-ri') + '-' + attribute + '-' + control.getAttribute(attribute));
          }
        });
      });

      // 換掉這一列的規則（家族內換對象時用）：保留 join；新規則不帶卡片身分，換掉條件後重算命名。
      function replaceRule(build) {
        var draft = Store.getState().filter.draft;
        var old = currentRules[ri];
        var fresh = build(old);
        fresh.join = old.join;
        if (['accountSide','accountPair','specialAccountCategoryPair'].indexOf(old.type) >= 0 &&
            ['accountSide','accountPair','specialAccountCategoryPair'].indexOf(fresh.type) >= 0) {
          fresh.categorySelection = old.categorySelection || 'role';
        }
        keepMotionKey(old, fresh);
        currentRules[ri] = fresh;
        if (FilterStep.selectedRule === old) { FilterStep.selectedRule = fresh; }
        refreshAutomaticMetadata(draft, true);
        Store.setFilterDraft(draft);
      }
      Ui.FilterValues.bind(row.querySelector('.value-editor'), currentRule, Store.getState(), function (structural) {
        if (structural) {
          // 比較方式改變可能讓卡片帶入的條件變成別的條件，例如非營業日括號內改成週末。
          var structuralDraft = Store.getState().filter.draft;
          refreshAutomaticMetadata(structuralDraft, false);
          Store.setFilterDraft(structuralDraft);
        }
        else { patchCurrent({}); softRefreshReadback(container); softExpirePreviewPane(container); softRefreshGate(container); }
      });

      // 風險樣態：下拉只列同一個來源的條件。預篩選條件之間只換規則鍵；其他型別重建那一列。
      // 從常用篩選條件的卡片加入的條件換成另一張卡的型別時，改記成那一張卡，卡片的已選狀態與預設動機跟著換。
      var patternSubject = row.querySelector('[data-pattern-subject]');
      if (patternSubject) {
        patternSubject.addEventListener('change', function () {
          var value = patternSubject.value;
          var old = currentRules[ri];
          if (value.indexOf('prescreen:') === 0) {
            var key = value.slice('prescreen:'.length);
            if (old.type === 'prescreen') {
              patchCurrent({ prescreenKey: key });
              refreshAutomaticMetadata(Store.getState().filter.draft, false);
              Store.touch();
              return;
            }
            replaceRule(function () { var fresh = Ui.newFilterCondition('prescreen'); fresh.prescreenKey = key; return fresh; });
            return;
          }
          if (value === old.type) { return; }
          replaceRule(function () {
            var fresh = Ui.newFilterCondition(value);
            var card = commonTypeCard(value);
            if (old[COMMON_CARD_KEY] && card) { fresh[COMMON_CARD_KEY] = card.key; }
            return fresh;
          });
        });
      }
      row.querySelector('[data-action="remove-rule"]').addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        focusAfterRuleRemoval(currentRules, ri, gi, row);
        currentRules.splice(ri, 1);
        // 手改的名稱與動機由旗標保留；自動文字隨剩下的條件重算。
        refreshAutomaticMetadata(draft, true);
        Store.setFilterDraft(draft);
      });

      // 分類多選：同一側的所有已勾選身分組成陣列（去重、順序不影響判定）。
      // 陣列存在時即權威，legacy 單選欄位不再參與，故不回寫 scalar。
      row.querySelectorAll('[data-category-picker]').forEach(function (picker) {
        Ui.bindCategoryPicker(picker, Store.getState(), categoryPickerView(currentRules[ri], picker.getAttribute('data-category-picker')));
      });
      row.querySelectorAll('[data-category-bind]').forEach(function (checkbox) {
        checkbox.addEventListener('change', function () {
          var key = checkbox.getAttribute('data-category-bind');
          var selected = [];
          row.querySelectorAll('[data-category-bind="' + key + '"]').forEach(function (item) {
            if (item.checked && selected.indexOf(item.value) < 0) { selected.push(item.value); }
          });
          var patch = {};
          patch[key] = selected;
          patchCurrent(patch);
          softRefreshReadback(container);
          softExpirePreviewPane(container);
          softRefreshGate(container);
        });
      });

      // 借貸科目組合的模式跨兩個 wire 型別：換型別時重建規則、保留兩側分類與 join；同型別只 patch pairMode。
      var pairSelection = row.querySelector('[data-pair-selection]');
      if (pairSelection) {
        pairSelection.addEventListener('change', function () {
          var parts = pairSelection.value.split('|'), draft = Store.getState().filter.draft;
          var old = currentRules[ri];
          if (parts[0] !== old.type) {
            var fresh = Ui.newFilterCondition(parts[0]);
            fresh.join = old.join; fresh.pairMode = parts[1]; fresh.categorySelection = old.categorySelection;
            fresh.debitCategoryIds = (old.debitCategoryIds || []).slice(); fresh.creditCategoryIds = (old.creditCategoryIds || []).slice();
            if (old[COMMON_CARD_KEY]) { fresh[COMMON_CARD_KEY] = old[COMMON_CARD_KEY]; }
            keepMotionKey(old, fresh);
            currentRules[ri] = fresh;
            if (FilterStep.selectedRule === old) { FilterStep.selectedRule = fresh; }
            refreshAutomaticMetadata(draft, false);
            Store.setFilterDraft(draft);
          } else {
            patchCurrent({ pairMode: parts[1] });
            refreshAutomaticMetadata(Store.getState().filter.draft, false);
            Store.touch();
          }
        });
      }
      row.querySelectorAll('[data-rule-bind]').forEach(function (control) {
        var key = control.getAttribute('data-rule-bind');
        // 值編輯用 input（即時）：patchFilterCondition 只 patch、不重建面板（保住焦點），並即時刷新藍色
        // read-back，讓「這個情境會找出…」隨輸入同步更新（修：原本 change 要等下次重繪才更新）。
        // 同時把預覽面板軟更新為失效空狀態——read-back 顯示新值而預覽停留舊命中數是同屏矛盾。
        control.addEventListener('input', function () {
          var patch = {};
          patch[key] = key === 'values' ? control.value.split(/\r?\n/) : control.value;
          patchCurrent(patch);
          var keywordCounter = row.querySelector('[data-keyword-count]');
          if (keywordCounter) { keywordCounter.textContent = keywordCountText(currentRules[ri]); }
          // 結構性選擇（攸關資料元素欄位、比較方式、配對模式）會改變這一列該顯示哪些輸入框，
          // 因此重繪面板；一般值編輯仍只 patch，以保住輸入焦點。
          if (STRUCTURAL_RULE_KEYS.indexOf(key) >= 0) {
            Store.touch();
            return;
          }
          softRefreshReadback(container);
          softExpirePreviewPane(container);
          softRefreshGate(container);
        });
      });
    });

  }

  // 掛到共用的 FilterStep，供其他檔案與測試取用。
  FilterStep.dropRuleBadge = dropRuleBadge;
  FilterStep.RULE_FAMILIES = RULE_FAMILIES;
  FilterStep.PATTERN_PARAM_TYPES = PATTERN_PARAM_TYPES;
  FilterStep.patternTypeOptions = patternTypeOptions;
  FilterStep.ruleFamily = ruleFamily;
  FilterStep.isNonBusinessDayBracket = isNonBusinessDayBracket;
  FilterStep.isLegacyWeekendHolidayBracket = isLegacyWeekendHolidayBracket;
  FilterStep.joinToggleHtml = joinToggleHtml;
  FilterStep.ruleJoinHtml = ruleJoinHtml;
  FilterStep.setWellHtml = setWellHtml;
  FilterStep.interSetConnectorHtml = interSetConnectorHtml;
  FilterStep.setsHtml = setsHtml;
  FilterStep.exprJoin = exprJoin;
  FilterStep.effectiveConditionJoin = effectiveConditionJoin;
  FilterStep.hasMixedEffectiveConditionJoins = hasMixedEffectiveConditionJoins;
  FilterStep.groupReadBackExpressionHtml = groupReadBackExpressionHtml;
  FilterStep.readBackHtml = readBackHtml;
  FilterStep.readBackExpressionHtml = readBackExpressionHtml;
  FilterStep.readBackPlainText = readBackPlainText;
  FilterStep.scenarioBuilderHtml = scenarioBuilderHtml;
  FilterStep.ruleRowHtml = ruleRowHtml;
  FilterStep.commonDescriptionHtml = commonDescriptionHtml;
  FilterStep.compoundRuleHtml = compoundRuleHtml;
  FilterStep.requirementHintHtml = requirementHintHtml;
  FilterStep.patternSource = patternSource;
  FilterStep.commonTypeCard = commonTypeCard;
  FilterStep.ruleSubjectHtml = ruleSubjectHtml;
  FilterStep.bindGroupSelection = bindGroupSelection;
  FilterStep.focusAfterRuleRemoval = focusAfterRuleRemoval;
  FilterStep.bindCompoundRule = bindCompoundRule;
  FilterStep.legacyRulePolicyHtml = legacyRulePolicyHtml;
  FilterStep.ruleCategoryIds = ruleCategoryIds;
  FilterStep.categoryIdsLabel = categoryIdsLabel;
  FilterStep.categorySelectionHtml = categorySelectionHtml;
  FilterStep.pairSelectionIds = pairSelectionIds;
  FilterStep.categorySelectionShown = categorySelectionShown;
  FilterStep.categorySelectionModeLabel = categorySelectionModeLabel;
  FilterStep.actualCategoryLabels = actualCategoryLabels;
  FilterStep.pairActualCategories = pairActualCategories;
  FilterStep.keywordValues = keywordValues;
  FilterStep.keywordCountText = keywordCountText;
  FilterStep.keywordCountHtml = keywordCountHtml;
  FilterStep.ruleControlsHtml = ruleControlsHtml;
  FilterStep.ruleTypeControlsHtml = ruleTypeControlsHtml;
  FilterStep.isExplicitVoucherCondition = isExplicitVoucherCondition;
  FilterStep.containsExplicitVoucherCondition = containsExplicitVoucherCondition;
  FilterStep.quarterEndSummary = quarterEndSummary;
  FilterStep.ruleSummaryLabel = ruleSummaryLabel;
  FilterStep.conditionTargetHtml = conditionTargetHtml;
  FilterStep.STRUCTURAL_RULE_KEYS = STRUCTURAL_RULE_KEYS;
  FilterStep.bindConditionRows = bindConditionRows;
})(window);
