/*
  第五步：進階條件篩選。
  前端只組裝 AST 與渲染；條件由後端轉參數化 SQL 評估，前端不計算規則。

  條件入口與本次篩選並排；命名在保存時出現。已保存情境和矩陣另有檢視入口。
  純檢視狀態留在第五步的檔案裡，切換不改 AST；常用篩選條件、自訂條件與報告的業務判定由後端負責。

  第五步的程式分成五個檔案，index.html 依序載入：
  filter-step-view.js（檢視狀態與資料世代）、filter-step-sources.js（條件來源的挑選）、filter-step-rules.js（條件列與摘要文字）、
  filter-step-saved.js（已存情境、矩陣與條件篩選報告），以及本檔（送出形狀、送出前檢查、畫面組裝與事件綁定的順序）。
  各檔把自己的最外層函式與常數掛在 JetUi.FilterStep，其他檔案透過它轉呼叫；共用的檢視狀態也放在那裡。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;
  // 第五步五個檔案共用的函式與檢視狀態；各檔在結尾把自己的函式掛上去。
  var FilterStep = Ui.FilterStep = Ui.FilterStep || {};
  // 其他檔案定義的函式：呼叫時才去 FilterStep 取，所以和載入順序無關。
  function forward(name) { return function () { return FilterStep[name].apply(null, arguments); }; }
  var selectedPopulationScope = forward('selectedPopulationScope');
  var setFieldError = forward('setFieldError');
  var clearFieldError = forward('clearFieldError');
  var dataGeneration = forward('dataGeneration');
  var sameGeneration = forward('sameGeneration');
  var syncViewState = forward('syncViewState');
  var ownRequirementNote = forward('ownRequirementNote');
  var rdeFieldValueType = forward('rdeFieldValueType');
  var prescreenRequirementNote = forward('prescreenRequirementNote');
  var hasCommonMarker = forward('hasCommonMarker');
  var scenarioSource = forward('scenarioSource');
  var requiresScenarioMetadata = forward('requiresScenarioMetadata');
  var fillSmoothly = forward('fillSmoothly');
  var smoothScroll = forward('smoothScroll');
  var commonPickerHtml = forward('commonPickerHtml');
  var customPickerHtml = forward('customPickerHtml');
  var prescreenPickerHtml = forward('prescreenPickerHtml');
  var effectiveRuleJoin = forward('effectiveRuleJoin');
  var readBackHtml = forward('readBackHtml');
  var populationScopeHtml = forward('populationScopeHtml');
  var syncSuggestedMetadata = forward('syncSuggestedMetadata');
  var scenarioBuilderHtml = forward('scenarioBuilderHtml');
  var savedFeedbackText = forward('savedFeedbackText');
  var draftContentKey = forward('draftContentKey');
  var belongsToTarget = forward('belongsToTarget');
  var saveFailureText = forward('saveFailureText');
  var filterCommitSnapshot = forward('filterCommitSnapshot');
  var acceptsFilterCommit = forward('acceptsFilterCommit');
  var completeScenarioSave = forward('completeScenarioSave');
  var keywordValues = forward('keywordValues');
  var savedScenariosHtml = forward('savedScenariosHtml');
  var criteriaReportHtml = forward('criteriaReportHtml');
  var tagMatrixHtml = forward('tagMatrixHtml');
  var paneButton = forward('paneButton');
  var sourceButton = forward('sourceButton');
  var conditionPaneAttributes = forward('conditionPaneAttributes');
  var conditionTargetHtml = forward('conditionTargetHtml');
  var restoreViewState = forward('restoreViewState');
  var bindWorkspacePanes = forward('bindWorkspacePanes');
  var bindConditionSources = forward('bindConditionSources');
  var bindConditionRows = forward('bindConditionRows');
  var bindSavedScenarios = forward('bindSavedScenarios');
  // 其他檔案定義的常數：只讀，定義它的檔案先載入。
  var draftResponseGuard = FilterStep.draftResponseGuard;
  var COMMON_CARD_KEY = FilterStep.COMMON_CARD_KEY;

  // wire 投影（單一收斂點）：把一個情境（草稿或已存）深拷貝成 filter.preview / filter.commit 的
  // wire 形狀——{ source?:'common', name, rationale, groups }，且 groups 內每條 rule 都遞迴剝除 __commonCard 等
  // 任何 UI-only 鍵。深拷貝是關鍵：淺取 groups 會讓 wire 仍指向帶標記的 rule 物件。所有送往後端的
  // scenario/draft 一律經此，確保 UI-only 標記絕不外洩到 wire；明確剝除，不假設後端會忽略多餘欄位。
  function toWireScenario(s) { return projectWireScenario(s, false); }

  // isDraft：編輯器草稿才依名稱與動機旗標產生命名來源。已儲存但沒有命名來源的舊情境原樣送回，
  // 不在儲存或移除其他情境時被記成系統產生（規格：舊情境沒有命名來源時保留原文）。
  function projectWireScenario(s, isDraft) {
    // 條件組依畫面順序送出，不再另排任何組。
    var ordered = s.groups || [];
    var wire = {
      name: s.name,
      rationale: s.rationale,
      // 過濾掉沒有條件的群組：使用者「＋ 另一組條件」建到一半的空 set 不送出，後端不會報「群組沒有規則」。
      groups: ordered.filter(function (g) { return (g.rules || []).length > 0; }).map(function (g) {
        return {
          join: g.join,
          matchScope: g.matchScope,
          rules: (g.rules || []).map(function (r) {
            var clean = {};
            Object.keys(r).forEach(function (k) {
              // 剝除所有 __ 前綴鍵（UI-only 慣例，如 __commonCard）——按前綴而非逐鍵點名，未來新增
              // UI 鍵不會漏剝而洩進 wire。後端儲存時雖只留各條件類型用得到的欄位（FilterRuleWireFields.cs），
              // 預覽、查詢與草稿是否已儲存的比對也都用這份資料，畫面標記一律不送出。
              if (k.indexOf('__') !== 0) {
                clean[k] = Array.isArray(r[k]) ? r[k].slice() : r[k];
              }
            });
            return clean.type === 'typed' ? typedWireRule(clean) : clean;
          })
        };
      })
    };
    // 2026-09-05 到 06 那一輪保存的情境可能還帶 exclusions；已存情境的摘要照原樣帶出，編輯器畫成唯讀的「舊排除區」。
    // 草稿帶著就原樣送出，讓後端以 invalid_scenario 指路改用否定模式，不在畫面默默丟掉；審計員按「移除」後才不再送。
    // 儲存或移除別的情境時不回送已存情境，資料庫裡帶排除區的舊情境原文保留，列在待修正清單，直到審計員修改或移除它。
    if (Array.isArray(s.exclusions) && s.exclusions.length) { wire.exclusions = s.exclusions; }
    var source = scenarioSource(s);
    var originGroups = ordered.filter(function (g) { return (g.rules || []).length > 0; });
    if (s.editorOrigins && !s.__restoredOrigins) { wire.editorOrigins = JSON.parse(JSON.stringify(s.editorOrigins)); }
    // 第 2 版的編輯來源逐條記卡片代號（2026-10-29 起）；第 1 版記的是字母，這一版只原樣送回、不再讀取。
    // 舊版情境重新編輯後一律改存第 2 版，卡片要審計員重新點選（見 filter-step-saved 的 editScenario）。
    else if (hasCommonMarker(s) || s.__restoredOrigins || isDraft) {
      wire.editorOrigins = { version: 2,
        nameIsAutomatic: !s.__nameDirty, rationaleIsAutomatic: !s.__rationaleDirty, groups: originGroups.map(function (g) {
        return { cards: g.rules.map(function (r) { return r[COMMON_CARD_KEY] || null; }) };
      }) };
    }
    wire.groups.forEach(function (group) {
      group.rules = group.rules.map(nestedWireRule);
    });
    if (source) { wire.source = source; }
    return wire;
  }

  // 攸關資料元素條件的送出形狀：operand carrier 必須與比較方式一致，帶了不適用的欄位
  // 會被系統端擋下。因此這裡只保留該比較方式真正使用的那一個 carrier，
  // 並且只有金額型欄位才附比較基準。
  function typedWireRule(clean) {
    var carrier = Ui.typedOperatorCarrier(clean.operator);
    var wire = {
      join: clean.join,
      type: 'typed',
      fieldId: clean.fieldId,
      operator: clean.operator
    };
    if (carrier === 'value') { wire.value = clean.value; }
    if (carrier === 'range') { wire.from = clean.from; wire.to = clean.to; }
    if (carrier === 'set') { wire.values = (clean.values || []).slice(); }
    if (rdeFieldValueType(clean.fieldId) === 'money') {
      wire.amountBasis = clean.amountBasis;
    }
    return wire;
  }

  function nestedWireRule(rule) {
    var clean = {};
    Object.keys(rule).forEach(function (key) { if (key.indexOf('__') !== 0) { clean[key] = rule[key]; } });
    if (clean.rules) { clean.rules = clean.rules.map(nestedWireRule); }
    if (clean.type === 'fieldValue') { return Ui.FilterValues.wire(clean, Store.getState()); }
    if (['customKeywords', 'trailingDigits', 'text'].indexOf(clean.type) >= 0) {
      clean.keywords = keywordValues(clean).join(',');
    }
    return clean.type === 'typed' ? typedWireRule(clean) : clean;
  }

  // 群組組合器：群組內規則 join 的一致值——任一規則為 OR 即「任一(OR)」，否則「全部(AND)」。空群組
  // 回退「全部」。組合器＝把群組內各規則 join 設為同值，後端逐條 join 評估等價（全 AND＝符合全部、全 OR＝任一）。
  function groupCombinator(group) {
    var rules = group.rules.length > 1 ? group.rules.slice(1) : group.rules;
    return rules.some(function (r) { return effectiveRuleJoin(r) === 'OR'; }) ? 'OR' : 'AND';
  }

  // 舊 AST 省略 matchScope 時維持 row；只有明示 sameVoucher 才切換傳票錨定呈現。
  // 未知非空值仍由 wire 原樣送回後端 validator，不在前端靜默修正。
  function groupMatchScope(group) {
    return group && group.matchScope === 'sameVoucher' ? 'sameVoucher' : 'row';
  }

  // 有效組間運算子（單一情境層運算子的唯一推導點）：段控顯示、read-back、新組繼承、wire normalize
  // 四處一律讀這裡，保證「畫面顯示＝實際送出」。取值：第二組存在時取其 join（後端 left-fold
  // 下第一組的 join 沒有左運算元、不參與語意），否則預設 'OR'——與段控首次出現（加到第二組）時的
  // 顯示一致。
  function scenarioJoin(draft) {
    var groups = draft.groups || [];
    return groups[1] ? effectiveRuleJoin(groups[1]) : 'OR';
  }

  // 草稿送出點專用投影：先把所有條件組的 join 收斂成有效組間運算子（單一情境層運算子模型），再走
  // toWireScenario。這保證任何未預見的操作路徑下「顯示＝送出」都成立；只整理送出的副本，不在預覽
  // 或儲存時改寫草稿。已儲存情境重開時帶 __preserveJoins，維持原有各組連接方式，不在此收斂。
  function toWireDraft(draft) {
    if (draft.__preserveJoins) { return projectWireScenario(draft, true); }
    var join = scenarioJoin(draft);
    // 只整理送出的請求；預覽或儲存時不改動編輯中的草稿。
    var projected = Object.assign({}, draft, { groups: (draft.groups || []).map(function (group) {
      return Object.assign({}, group, { join: join });
    }) });
    return projectWireScenario(projected, true);
  }

  function previewPaneHtml(preview) {
    var body;
    if (!preview) {
      var filter = Store.getState().filter;
      var hasConditions = filter.draft.groups.some(function (group) { return group.rules.length > 0; });
      body = '<p class="empty-state">' + (filter.previewExpired && hasConditions
        ? '條件已變更。按「查看符合的傳票」更新結果。'
        : '選好條件後，按「查看符合的傳票」。') + '</p>';
    } else {
      body = Ui.FilterVouchers.summary(preview);
    }

    // 內容區獨立節點＋data-empty 標記：供值編輯時軟失效抽換（softExpirePreviewPane），不重建整面板。
    return (
      '<section class="rule-card">' +
        '<h3 class="rule-card__title" data-preview-title tabindex="-1">預覽結果</h3>' +
        '<div data-bind="preview-pane-body" data-motion-key="preview-pane-body"' + (preview ? '' : ' data-empty="1"') + '>' + body + '</div>' +
      '</section>'
    );
  }

  function render(container, state) {
    syncViewState(state); // 專案切換／已存清單替換時對齊檢視狀態（先於任何讀取 viewState 的 HTML 生成）

    if (!state.project) {
      container.innerHTML = Ui.noProjectPanel('進階條件篩選');
      Ui.bindNoProjectPanel(container);
      return;
    }
    var draft = state.filter.draft;
    var saved = state.filter.savedScenarios;
    container.innerHTML =
      '<div class="panel panel--wide filter-workspace" data-motion-key="filter-panel">' +
        '<h2 class="panel__title">進階條件篩選</h2>' +
        populationScopeHtml(state) +
        '<div class="filter-work-tabs" role="tablist" aria-label="篩選工作區">' +
          paneButton('filter', '篩選與檢視') + paneButton('saved', '已儲存情境與矩陣（' + saved.length + '）') + '</div>' +
        '<section data-filter-pane="filter" id="filter-pane-filter" role="tabpanel" aria-labelledby="filter-tab-filter" data-motion-pane data-motion-key="filter-pane-filter"' + (FilterStep.viewState.workspacePane === 'filter' ? '' : ' hidden') + '>' +
          '<div class="filter-workbench"><aside class="filter-entry" tabindex="-1" aria-label="加入篩選條件">' +
            '<div class="filter-entry-heading"><h3 class="filter-entry-title">條件總覽</h3></div>' + conditionTargetHtml(draft) +
            '<div class="filter-source-tabs" role="tablist" aria-label="條件來源">' + sourceButton('common', '常用篩選條件') + sourceButton('prescreen', '預篩選') +
              sourceButton('custom', '自訂篩選條件') + '</div>' +
            '<div class="filter-entry-scroll" data-preserve-scroll="filter-condition-catalog">' +
            '<div' + conditionPaneAttributes('common') + '>' + commonPickerHtml(draft) + '</div>' +
            '<div' + conditionPaneAttributes('prescreen') + '>' + prescreenPickerHtml() + '</div>' +
            '<div' + conditionPaneAttributes('custom') + '>' + customPickerHtml() + '</div>' +
          '</div></aside><div class="filter-current">' + scenarioBuilderHtml(draft) + previewPaneHtml(state.filter.preview) + '</div></div>' +
        '</section>' +
        '<section data-filter-pane="saved" id="filter-pane-saved" role="tabpanel" aria-labelledby="filter-tab-saved" data-motion-pane data-motion-key="filter-pane-saved"' + (FilterStep.viewState.workspacePane === 'saved' ? '' : ' hidden') + '>' +
          savedScenariosHtml(state) + tagMatrixHtml(state) + criteriaReportHtml(state) + '</section>' +
        Ui.stepFooterHtml(state) + '</div>';

    bind(container);
    restoreViewState(container);
  }

  // 後端 FilterScenarioLimits 的就近 UI 鏡像；只檢查 wire 陣列長度，不截斷、不 trim、
  // 不做任何命中語意。preview/save 仍會由後端以同一上限與完整 SQL plan 預算驗證。
  function hasOversizedTextSet(draft) {
    return draft.groups.some(function (group) {
      return group.rules.some(function (rule) {
        return rule.type === 'textSet' && Array.isArray(rule.values) &&
          rule.values.length > Ui.TEXT_SET_MAX_VALUES;
      });
    });
  }

  // 尚未選欄位或比較方式的攸關資料元素條件：先在畫面擋下，避免送出必被拒絕的形狀。
  function hasIncompleteTypedRule(draft) {
    return draft.groups.some(function (group) {
      return group.rules.some(function (rule) {
        return rule.type === 'typed' && (!rule.fieldId || !rule.operator);
      });
    });
  }

  // 攸關資料元素條件的值清單上限（與後端相同，1 到 100）；後端仍是權威。
  function hasOversizedTypedSet(draft) {
    return draft.groups.some(function (group) {
      return group.rules.some(function (rule) {
        return rule.type === 'typed' && Array.isArray(rule.values) &&
          rule.values.length > Ui.TYPED_SET_MAX_VALUES;
      });
    });
  }

  // 條件在畫面上的位置：第幾組第幾條；條件括號或傳票條件內的子條件再寫「的第幾項」。
  // 組號與條件組標頭「第 N 組」相同（草稿順序），條件號是組內順序。
  function conditionPositionText(gi, path) {
    var text = '第 ' + (gi + 1) + ' 組第 ' + (path[0] + 1) + ' 條';
    for (var i = 1; i < path.length; i++) { text += '的第 ' + (path[i] + 1) + ' 項'; }
    return text;
  }

  // 單一條件在畫面上就能看出的缺值或錯誤（權威驗證仍在後端）。回傳條件名稱與原因，沒有問題回 null。
  // 原因若以條件名稱開頭就去掉重複的名稱，讓訊息讀成「第 1 組第 2 條「總帳日期」請填入……」。
  function ruleProblem(rule) {
    var label = '', message = '';
    // 條件本身缺前置條件時，寫後端同一句原因，和後端驗證錯誤的「第幾組第幾條：原因」一樣不重複條件名稱。
    var requirement = rule.type === 'prescreen' ? prescreenRequirementNote(rule.prescreenKey) : ownRequirementNote(rule);
    if (requirement) { return { label: '', message: requirement }; }
    if (rule.type === 'fieldValue') {
      message = Ui.FilterValues.problem(rule, Store.getState());
      label = (Ui.FilterValues.field(rule, Store.getState()) || {}).label || '';
    } else if (rule.type === 'specialAccountCategoryPair') {
      label = '借貸科目組合';
      if (!(rule.debitCategoryIds || []).length) { message = '尚未選擇借方分類'; }
      else if (!(rule.creditCategoryIds || []).length) { message = '尚未選擇貸方分類'; }
    } else if (rule.type === 'entityFrequency') {
      label = '科目與人員統計';
      function countIsValid(value) { return /^\+?\d+$/.test(String(value == null ? '' : value).trim()) && Number(value) <= 2147483647; }
      if (!countIsValid(rule.countFrom)) { message = '次數須為 0 以上的整數'; }
      else if (rule.countOperator === 'between' && !countIsValid(rule.countTo)) { message = '上限須為 0 以上的整數'; }
      else if (rule.countOperator === 'between' && Number(rule.countTo) < Number(rule.countFrom)) { message = '上限小於下限'; }
    } else if (rule.type === 'dateRange' && rule.from && rule.to && rule.from > rule.to) {
      label = (Ui.FILTER_RULE_TYPES.find(function (item) { return item.value === 'dateRange'; }) || {}).label || '';
      message = '起日晚於迄日';
    } else if (rule.type === 'textSet' && Array.isArray(rule.values) && rule.values.length > Ui.TEXT_SET_MAX_VALUES) {
      // 清單超過上限時逐條寫出是哪一條條件，和欄位條件同一句（使用者 2026-10-14 第三輪勾選頁確認 R3-08）。
      label = (Ui.FILTER_RULE_TYPES.find(function (item) { return item.value === 'textSet'; }) || {}).label || '';
      message = '請填 1 到 ' + Ui.TEXT_SET_MAX_VALUES + ' 個值';
    } else if (rule.type === 'typed' && Array.isArray(rule.values) && rule.values.length > Ui.TYPED_SET_MAX_VALUES) {
      label = rule.fieldId && rdeFieldValueType(rule.fieldId) ? Ui.rdeFieldLabel(Store.getState(), rule.fieldId) : '';
      message = '請填 1 到 ' + Ui.TYPED_SET_MAX_VALUES + ' 個值';
    }
    if (!message) { return null; }
    if (label && message.indexOf(label) === 0) { message = message.slice(label.length); }
    return { label: label, message: message };
  }

  // 依畫面順序列出草稿中每一條有問題的條件，含條件括號與傳票條件內的子條件。
  function draftRuleProblems(draft) {
    var found = [];
    function visit(rules, gi, path) {
      (rules || []).forEach(function (rule, ri) {
        var at = path.concat(ri);
        var problem = ruleProblem(rule);
        if (problem) {
          found.push({ gi: gi, path: at, text: conditionPositionText(gi, at) +
            (problem.label ? '「' + problem.label + '」' : '：') + problem.message });
        }
        if (rule.type === 'group' || rule.type === 'voucher') { visit(rule.rules, gi, at); }
      });
    }
    (draft.groups || []).forEach(function (group, gi) { visit(group.rules, gi, []); });
    return found;
  }

  // 某條條件要填值的輸入欄：先找值或日期的輸入欄，找不到再退回該列第一個可用控制項。
  function ruleValueControl(gi, path) {
    var row = document.querySelector('.filter-workspace .rule-row[data-gi="' + gi + '"][data-ri="' + path.join('.') + '"]');
    if (!row || !row.getClientRects().length) { return null; }
    var visible = function (control) { return control.getClientRects().length > 0; };
    var value = Array.from(row.querySelectorAll('[data-date-input], [data-value-key="values"], [data-value-key="daysOfMonth"], ' +
      '[data-value-key="value"], [data-value-key="from"], [data-value-key="to"], [data-rule-bind="from"], [data-rule-bind="to"]'))
      .filter(function (control) { return !control.disabled && control.closest('.rule-row') === row; }).find(visible);
    if (value) { return value; }
    return Array.from(row.querySelectorAll('.rule-row__controls input:not([disabled]), .rule-row__controls select:not([disabled]), .rule-row__controls textarea:not([disabled]), .rule-row__controls button:not([disabled])'))
      .filter(function (control) { return control.closest('.rule-row') === row; }).find(visible) || null;
  }

  // 預覽／保存前的前端引導：名稱、動機或至少一條件未補齊時，於欄位旁（紅框＋紅字）
  // 與按鈕上方（form-notice）就近提示並不送出。純呈現——後端 invalid_scenario 仍是權威。
  // 純自訂情境名稱/動機必填；有常用篩選條件卡片的草稿只要仍有標記就可留白，後端保存時產生正準留痕替補。
  function scenarioGate(container, includeSavedScenarios, skipDuplicateName) {
    var draft = Store.getState().filter.draft;
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
    var notice = container.querySelector('[data-bind="scenario-notice"]');

    var nameError = '';
    var rationaleError = '';

    var nameEmpty = !draft.name || !draft.name.trim();
    var rationaleEmpty = !draft.rationale || !draft.rationale.trim();
    var metadataRequired = includeSavedScenarios && requiresScenarioMetadata(draft);
    var noCondition = (draft.groups.length === 0 ||
      draft.groups.every(function (g) { return g.rules.length === 0; }));
    var incompleteTyped = hasIncompleteTypedRule(draft);

    // 每條缺值都寫出第幾組第幾條與條件名稱（W20），審計員不必逐條找。
    var problems = draftRuleProblems(draft).map(function (item) { return item.text; });
    if (metadataRequired && nameEmpty) { nameError = '請先填寫情境名稱'; problems.push('情境名稱'); }
    if (metadataRequired && rationaleEmpty) { rationaleError = '請先填寫篩選動機說明'; problems.push('篩選動機'); }
    if (noCondition) { problems.push('至少一條篩選條件'); }
    if (incompleteTyped) {
      problems.push('攸關資料元素欄位條件需選定欄位與比較方式');
    }

    // 名稱不可和其他已儲存情境相同；後端仍是權威，這裡先在名稱欄旁說明，不必送出才知道。
    // 另存副本時名稱會另附「（副本）」，不在這裡比對。
    var duplicate = includeSavedScenarios && !skipDuplicateName ? duplicateScenarioName(draft) : '';
    var duplicateText = duplicate ? '已有同名情境「' + duplicate + '」，請改用其他名稱。' : '';
    if (duplicate) { nameError = duplicateText; }
    // 已經顯示的錯誤說明直接換字，不先收起再長出來；補齊的才收回去。
    if (nameError) { setFieldError(nameInput, nameError); } else { clearFieldError(nameInput); }
    if (rationaleError) { setFieldError(rationaleInput, rationaleError); } else { clearFieldError(rationaleInput); }

    // 提示收起時字留著，跟著一起收回去，不會先變成一個空框。
    if (problems.length === 0 && !duplicate) {
      if (notice) { notice.hidden = true; }
      return true;
    }
    if (notice) {
      notice.textContent = (problems.length ? '尚需補齊：' + problems.join('、') + (duplicate ? '。' : '') : '') + duplicateText;
      // 同一個提示框也用來寫琥珀色的提醒（見 showSaveNotice）；尚需補齊維持原本的紅色。
      notice.classList.remove('form-notice--warning');
      notice.hidden = false;
    }
    // 聚焦第一個有問題的欄位（名稱 → 動機）；只缺條件時不搶焦點。
    if ((metadataRequired && nameEmpty) || duplicate) { nameInput.focus(); }
    else if (metadataRequired && rationaleEmpty) { rationaleInput.focus(); }
    return false;
  }

  // 和後端相同：去掉前後空白後逐字比對。編輯中的情境不和自己比。
  function duplicateScenarioName(draft) {
    var name = String(draft.name || '').trim();
    if (!name) { return ''; }
    var saved = Store.getState().filter.savedScenarios;
    var editing = draft.__editingSavedRef === saved ? draft.__editingIndex : null;
    var hit = saved.filter(function (scenario, i) { return i !== editing && String(scenario.name || '').trim() === name; })[0];
    return hit ? name : '';
  }

  // 使用者輸入時放寬（不在打字途中跳出新錯誤）——只清除已補齊欄位的錯誤、
  // 全部補齊時收起按鈕旁提示；不在此新增任何錯誤。
  function softRefreshGate(container) {
    var draft = Store.getState().filter.draft;
    var feedback = container.querySelector('[data-bind="save-feedback"]');
    var feedbackText = savedFeedbackText(draft);
    if (feedback && feedbackText) { feedback.innerHTML = feedbackText; feedback.hidden = false; }
    else if (feedback) { feedback.hidden = true; }
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
    var notice = container.querySelector('[data-bind="scenario-notice"]');
    var metadataRequired = requiresScenarioMetadata(draft);

    var duplicate = duplicateScenarioName(draft);
    if (draft.name && draft.name.trim() && !duplicate) { clearFieldError(nameInput); }
    if (draft.rationale && draft.rationale.trim()) { clearFieldError(rationaleInput); }

    var noCondition = (draft.groups.length === 0 ||
      draft.groups.every(function (g) { return g.rules.length === 0; }));
    var oversizedTextSet = hasOversizedTextSet(draft);
    var stillMissing = (metadataRequired && (!draft.name || !draft.name.trim())) ||
      (metadataRequired && (!draft.rationale || !draft.rationale.trim())) ||
      noCondition || oversizedTextSet || hasOversizedTypedSet(draft) || hasIncompleteTypedRule(draft) || !!duplicate;
    if (!stillMissing && notice) { notice.hidden = true; }
  }

  // 規則「值編輯」後即時刷新藍色 read-back：值編輯走 patchFilterRule（只 notify、不重建面板以保住輸入
  // 焦點），故 read-back（整段算好的 HTML）不會自己更新——這裡只抽換 .scenario-readback 這一段，不碰
  // 任何輸入框，焦點不受影響（read-back 是條件清單之後的獨立節點）。
  function softRefreshReadback(container) {
    syncSuggestedMetadata(container);
    var el = Ui.findLive(container, '.filter-readback .scenario-readback');
    if (!el) { return; }
    var html = readBackHtml(Store.getState().filter.draft);
    fillSmoothly(el.closest('.filter-readback'), function () {
      if (html) { el.outerHTML = html; }
      else if (el.parentNode) { el.parentNode.removeChild(el); }
    });
  }

  // 值編輯後把預覽面板軟更新為「已失效」空狀態：patchFilterRule 已把 Store 的預覽清掉（值編輯改變
  // 命中集合），但它只 notify 不重繪（保住輸入焦點），畫面上的舊命中數不會自己消失——這裡只抽換
  // 預覽面板的內容區，不碰任何輸入框。已是空狀態時直接返回（冪等，連續打字不重複動 DOM、不抖動）。
  function softExpirePreviewPane(container) {
    var body = Ui.findLive(container, '[data-bind="preview-pane-body"]');
    if (!body || body.getAttribute('data-empty') === '1') { return; }
    fillSmoothly(body, function () { body.innerHTML = '<p class="empty-state">條件已變更。按「查看符合的傳票」更新結果。</p>'; });
    body.setAttribute('data-empty', '1');
  }

  // 送出：另存副本、名稱與動機、預覽、儲存與就地標錯（原本 bind 裡的三段）。
  function bindSubmission(container) {
    var copyEdit = container.querySelector('[data-action="save-scenario-copy"]');
    if (copyEdit) { copyEdit.addEventListener('click', function () {
      saveScenario(true);
    }); }

    // 名稱／動機：值編輯只 patch（notify），不重建面板（保住輸入焦點）。
    // dirty 旗標：非空手改＝鎖住自動命名對該欄的覆寫；清空（含只剩空白）＝交還自動命名（refreshAutomaticMetadata）。
    // 手改範例帶入的文字後，該欄改由使用者負責，條件增減時不再改回自動。
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    nameInput.addEventListener('input', function () {
      Store.patchFilterDraftMeta({ name: nameInput.value, __nameDirty: !!nameInput.value.trim(), __templateName: false });
      softRefreshGate(container);
    });
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
    rationaleInput.addEventListener('input', function () {
      Store.patchFilterDraftMeta({ rationale: rationaleInput.value, __rationaleDirty: !!rationaleInput.value.trim(), __templateRationale: false });
      softRefreshGate(container);
    });

    container.querySelector('[data-action="preview-scenario"]').addEventListener('click', function () {
      if (!scenarioGate(container, false)) { return; }
      // 預覽失敗時，只有把原因標到條件列才算就地顯示；沒有標到時在步驟上方顯示提示框。
      var previewMarked = 0;
      Ui.run('預覽篩選情境', function () {
        previewMarked = 0;
        var previewState = Store.getState();
        var draft = previewState.filter.draft;
        var populationScope = selectedPopulationScope(previewState);
        var draftRevision = Store.getFilterDraftRev();
        var projectId = previewState.project ? previewState.project.projectId : null;
        var projectRef = previewState.project, generation = dataGeneration(previewState);
        var acceptResponse = draftResponseGuard.issue(function () {
          var latest = Store.getState();
          return !!latest.project && latest.project.projectId === projectId &&
            latest.project === projectRef && sameGeneration(dataGeneration(latest), generation) &&
            draftRevision === Store.getFilterDraftRev() &&
            selectedPopulationScope(latest) === populationScope;
        });
        // 送出剝除標記後的 wire 形狀（draft 內卡片帶入的 rule 帶 __commonCard，絕不可外洩）。
        return global.JetApi.filterPreview({
          populationScope: populationScope,
          scenario: toWireDraft(draft)
        }).then(function (data) {
          if (!acceptResponse()) { return null; }
          var request = { populationScope: populationScope, scenario: toWireDraft(draft) };
          return global.JetApi.queryFilterVoucherPage(Object.assign({}, request, { pageSize: Ui.TABLE_PAGE_SIZE })).then(function (page) {
            data.scenario.voucherPage = page; data.scenario.voucherRequest = request; return data;
          });
        }).then(function (data) {
          if (!data) { return; }
          if (!acceptResponse() || data.scenario.populationScope !== populationScope) { return; }
          Store.setFilterPreview(data.scenario);
          if (global.JetFocus) { global.JetFocus.defer(function () { return document.querySelector('.filter-workspace [data-preview-title]'); }); }
          return data.scenario;
        }).catch(function (error) { if (!acceptResponse()) { return; } previewMarked = markRuleErrors(container, error); throw error; });
      }, { inlineError: function () { return previewMarked > 0; }, completionMessage: function (scenario) {
        // 訊息句子是使用者 2026-10-13 勾選頁確認維持的原句；分錄數用和預覽摘要同一個 summaryRowCount。
        return scenario ? '情境預覽：符合條件 ' + scenario.summaryRowCount + ' 筆分錄，' + scenario.voucherCount + ' 張傳票。' : '';
      } });
    });

    // 後端 invalid_scenario 帶 details（第幾組、第幾條、原因）時，把該列標紅、就地寫原因並捲到第一列；
    // 沒有 details 的錯誤維持 Ui.run 的整段訊息。這只是呈現，判定權威仍在後端。回傳標紅的列數。
    // 後端的組號是送出後的順序；送出時略過空組，所以先換回畫面上的組位置。
    // targetPosition 是這次儲存的情境在清單上的位置；細節標明屬於別的情境時不標在目前草稿上。
    function markRuleErrors(root, error, targetPosition) {
      root.querySelectorAll('.rule-row--invalid').forEach(function (row) {
        row.classList.remove('rule-row--invalid');
        var old = row.querySelector('[data-rule-error]:not([data-motion-folding])');
        if (!old) { return; }
        // 舊的說明收回去再拿掉，新的從條件下面長出來（使用者 2026-10-13 要求出現與消失都有動態）。
        if (global.JetMotion && typeof global.JetMotion.fold === 'function') { global.JetMotion.fold(old, function () { old.remove(); }); } else { old.remove(); }
      });
      if (!error || !Array.isArray(error.details)) { return 0; }
      var sentGroups = [];
      Store.getState().filter.draft.groups.forEach(function (group, gi) { if ((group.rules || []).length) { sentGroups.push(gi); } });
      var first = null, marked = 0;
      error.details.forEach(function (detail) {
        if (!detail || typeof detail.group !== 'number' || typeof detail.rule !== 'number') { return; }
        if (!belongsToTarget(detail, targetPosition)) { return; }
        var draftGi = sentGroups[detail.group - 1];
        if (typeof draftGi !== 'number') { return; }
        var row = root.querySelector('.rule-row[data-gi="' + draftGi + '"][data-ri="' + (detail.rule - 1) + '"]');
        if (!row) { return; }
        marked++;
        row.classList.add('rule-row--invalid');
        var note = document.createElement('p');
        note.className = 'rule-row__error'; note.setAttribute('data-rule-error', ''); note.setAttribute('role', 'alert');
        note.textContent = detail.message;
        row.appendChild(note);
        if (global.JetMotion && typeof global.JetMotion.enter === 'function') { global.JetMotion.enter(note); }
        if (!first) { first = row; }
      });
      if (first) { first.scrollIntoView({ block: 'center', behavior: smoothScroll() }); }
      return marked;
    }

    // 儲存沒有送出或失敗時，原因寫在儲存按鈕上方，不只留在訊息紀錄。
    // 清單已變更與已滿 10 個只是需要注意，用琥珀色；儲存失敗用紅色（使用者 2026-10-13 裁定訊息分三級）。
    function showSaveNotice(text, warning) {
      var notice = container.querySelector('[data-bind="scenario-notice"]');
      if (!notice) { return; }
      if (text) { notice.textContent = text; notice.classList.toggle('form-notice--warning', !!warning); }
      notice.hidden = !text;
    }
    // 前端擋下、沒有送出的儲存不記「儲存篩選情境完成」。
    var SAVE_NOT_SENT = { notSent: true };

    // 回傳儲存作業的 Promise，讓「儲存並開啟」這類對話框等儲存結束再決定要不要繼續。
    // 只送這一個情境（使用者 2026-10-13 對「情境卡住」選「本計畫內修好」）：其他已存情境由後端照資料庫原樣保留，
    // 開案時無法套用目前規則的情境不會擋住這次儲存。
    function saveScenario(asCopy) {
      syncSuggestedMetadata(container);
      if (!scenarioGate(container, true, asCopy)) { return Promise.resolve(SAVE_NOT_SENT); }
      return Ui.run('儲存篩選情境', function () {
        var commitState = Store.getState();
        var current = commitState.filter;
        var populationScope = selectedPopulationScope(commitState);

        // 草稿過同一投影（深拷貝剝除 __commonCard，並保留 canonical source:'common'）。
        var editingIndex = asCopy ? null : current.draft.__editingIndex;
        var authored = toWireDraft(current.draft);
        if (asCopy) {
          var suffix = 1, originalName = authored.name;
          do { authored.name = originalName + '（副本' + (suffix === 1 ? '' : ' ' + suffix) + '）'; suffix++; }
          while (current.savedScenarios.some(function (scenario) { return scenario.name === authored.name; }));
          if (authored.editorOrigins) { authored.editorOrigins.nameIsAutomatic = false; }
        }
        // 存完後的清單只用來檢查上限；送出時只帶這一個情境。
        var scenarios = current.savedScenarios.slice();
        var position = null;
        if (typeof editingIndex === 'number') {
          if (current.draft.__editingSavedRef !== current.savedScenarios) {
            // 訊息面板與儲存鈕上方的提示用同一句（使用者 2026-10-13 勾選頁確認 59、60）。
            var staleText = '已儲存的情境清單已變更，請重新選擇要編輯的情境；目前的草稿仍保留。';
            Store.addMessage(staleText, 'warn'); showSaveNotice(staleText, true);
            return Promise.resolve(SAVE_NOT_SENT);
          }
          scenarios[editingIndex] = authored;
          position = editingIndex + 1;
        } else { scenarios.push(authored); }
        if (scenarios.length > Ui.FILTER_MAX_SAVED_SCENARIOS) {
          var limitText = '最多可儲存 ' + Ui.FILTER_MAX_SAVED_SCENARIOS + ' 個篩選情境，請先移除不需要的情境。';
          Store.addMessage(limitText, 'warn'); showSaveNotice(limitText, true);
          return Promise.resolve(SAVE_NOT_SENT);
        }
        // 新增或另存時加在最後，和後端的算法相同；修改時後端用 expectedName 確認畫面清單和資料庫是同一個情境。
        var targetPosition = position === null ? current.savedScenarios.length + 1 : position;
        var payload = { populationScope: populationScope, position: position, scenario: authored };
        if (position !== null) { payload.expectedName = current.savedScenarios[editingIndex].name; }
        var saveSnapshot = filterCommitSnapshot(commitState);
        return global.JetApi.filterScenarioSave(payload).then(function (data) {
          // 完成句由 Ui.run 接上耗時後記一則（使用者 2026-10-13 裁定成功時只留一則）；回應已過時就不記。
          return completeScenarioSave(saveSnapshot, data, data.position - 1, authored) ? data : null;
        }).catch(function (error) {
          if (!acceptsFilterCommit(saveSnapshot)) { return; }
          if (Store.getState().filter.draft === saveSnapshot.draft && Store.getFilterDraftRev() === saveSnapshot.revision) {
            // 能對到條件列的錯誤就地標紅；重名這類沒有位置的錯誤寫在儲存按鈕上方。
            if (!markRuleErrors(container, error, targetPosition)) { showSaveNotice(saveFailureText(error, targetPosition)); }
          }
          throw error;
        });
      }, {
        // 儲存失敗時，原因已經就地寫在條件列或儲存按鈕上方，不再另外在步驟上方顯示。
        inlineError: true,
        completionMessage: function (data) {
          return data && data !== SAVE_NOT_SENT ? '已儲存篩選情境（' + data.savedCount + ' / 10）。' : '';
        }
      });
    }
    container.querySelector('[data-action="save-scenario"]').addEventListener('click', function () { saveScenario(false); });
    FilterStep.saveDraftForReplacement = function () { return saveScenario(false); };

  }

  // 事件綁定分到五個檔案，各綁自己畫的那一塊；開頭幾行和各段的先後照原本的 bind。
  function bind(container) {
    Ui.bindReportArtifacts(container);
    syncSuggestedMetadata(container);
    // 自動名稱與卡片身分在上一行收斂後，才記下換成這份草稿時沒有修改的內容。
    if (FilterStep.viewState.cleanDraftKey === null) { FilterStep.viewState.cleanDraftKey = draftContentKey(Store.getState().filter.draft); }
    Ui.bindStepFooter(container);
    // 頁籤的方向鍵與「操作」選單的開關、鍵盤由 ui-core.js 的共用頁籤與選單處理。
    Ui.bindTabs(container);
    Ui.bindMenus(container);
    bindWorkspacePanes(container);
    bindConditionSources(container);
    bindConditionRows(container);
    bindSubmission(container);
    bindSavedScenarios(container);
  }

  // 掛到共用的 FilterStep，供其他檔案與測試取用。
  FilterStep.toWireScenario = toWireScenario;
  FilterStep.projectWireScenario = projectWireScenario;
  FilterStep.typedWireRule = typedWireRule;
  FilterStep.nestedWireRule = nestedWireRule;
  FilterStep.groupCombinator = groupCombinator;
  FilterStep.groupMatchScope = groupMatchScope;
  FilterStep.scenarioJoin = scenarioJoin;
  FilterStep.toWireDraft = toWireDraft;
  FilterStep.previewPaneHtml = previewPaneHtml;
  FilterStep.render = render;
  FilterStep.hasOversizedTextSet = hasOversizedTextSet;
  FilterStep.hasIncompleteTypedRule = hasIncompleteTypedRule;
  FilterStep.hasOversizedTypedSet = hasOversizedTypedSet;
  FilterStep.conditionPositionText = conditionPositionText;
  FilterStep.ruleProblem = ruleProblem;
  FilterStep.draftRuleProblems = draftRuleProblems;
  FilterStep.ruleValueControl = ruleValueControl;
  FilterStep.scenarioGate = scenarioGate;
  FilterStep.duplicateScenarioName = duplicateScenarioName;
  FilterStep.softRefreshGate = softRefreshGate;
  FilterStep.softRefreshReadback = softRefreshReadback;
  FilterStep.softExpirePreviewPane = softExpirePreviewPane;
  FilterStep.bindSubmission = bindSubmission;
  FilterStep.bind = bind;

  Ui.registerStep('filter', render);
})(window);
