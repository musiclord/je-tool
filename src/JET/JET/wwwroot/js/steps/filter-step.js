/*
  第五步：進階條件篩選。
  前端只組裝 AST 與渲染；條件由後端轉參數化 SQL 評估，前端不計算規則。

  條件入口與本次篩選並排；命名在保存時出現。已保存情境和高風險條件彙總另有檢視入口。
  純檢視狀態留在第五步的檔案裡，切換不改 AST；常用篩選條件、自訂條件與報告的業務判定由後端負責。

  第五步的程式分成五個檔案，index.html 依序載入：
  filter-step-view.js（檢視狀態與資料世代）、filter-step-sources.js（條件來源的挑選）、filter-step-rules.js（條件列與摘要文字）、
  filter-step-saved.js（已存情境、高風險條件彙總與條件篩選報告），以及本檔（送出形狀、送出前檢查、畫面組裝與事件綁定的順序）。
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
  var prescreenRequirementNote = forward('prescreenRequirementNote');
  var hasCommonMarker = forward('hasCommonMarker');
  var scenarioSource = forward('scenarioSource');
  var fillSmoothly = forward('fillSmoothly');
  var smoothScroll = forward('smoothScroll');
  var commonPickerHtml = forward('commonPickerHtml');
  var fieldPickerHtml = forward('fieldPickerHtml');
  var prescreenPickerHtml = forward('prescreenPickerHtml');
  var effectiveConditionJoin = forward('effectiveConditionJoin');
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
  var savedScenariosHtml = forward('savedScenariosHtml');
  var criteriaReportHtml = forward('criteriaReportHtml');
  var highRiskSummaryHtml = forward('highRiskSummaryHtml');
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

  // wire 投影（單一收斂點）：把一個情境（草稿或已存）深拷貝成 filter.preview 與 filter.scenario.save 的
  // wire 形狀 { source?:'common', name, rationale, groups }，groups 內每條 rule 都遞迴剝除 __commonCard 等
  // UI 專用鍵。深拷貝是關鍵：淺取 groups 會讓 wire 仍指向帶標記的 rule 物件。所有送往後端的
  // scenario/draft 一律經此，確保 UI-only 標記絕不外洩到 wire；明確剝除，不假設後端會忽略多餘欄位。
  function toWireScenario(s) { return projectWireScenario(s, false); }

  // isDraft：編輯器草稿才依名稱與動機旗標產生命名來源。已儲存但沒有命名來源的情境原樣送回，
  // 不在儲存或移除其他情境時被記成系統產生，名稱與動機保留原文。
  function projectWireScenario(s, isDraft) {
    // 條件組依畫面順序送出，不另排任何組。
    var ordered = s.groups || [];
    var wire = {
      name: s.name,
      rationale: s.rationale,
      // 過濾掉沒有條件的群組：按「條件組」建到一半的空 set 不送出，後端不會報「群組沒有規則」。
      groups: ordered.filter(function (g) { return (g.conditions || []).length > 0; }).map(function (g) {
        return {
          join: g.join,
          matchScope: g.matchScope,
          conditions: (g.conditions || []).map(function (r) {
            var clean = {};
            Object.keys(r).forEach(function (k) {
              // 剝除所有 __ 前綴鍵（UI-only 慣例，如 __commonCard）——按前綴而非逐鍵點名，未來新增
              // UI 鍵不會漏剝而洩進 wire。後端儲存時雖只留各條件類型用得到的欄位（FilterConditionWireFields.cs），
              // 預覽、查詢與草稿是否已儲存的比對也都用這份資料，畫面標記一律不送出。
              if (k.indexOf('__') !== 0) {
                clean[k] = Array.isArray(r[k]) ? r[k].slice() : r[k];
              }
            });
            return clean;
          })
        };
      })
    };
    var source = scenarioSource(s);
    var originGroups = ordered.filter(function (g) { return (g.conditions || []).length > 0; });
    if (s.editorOrigins && !s.__restoredOrigins) { wire.editorOrigins = JSON.parse(JSON.stringify(s.editorOrigins)); }
    // 編輯來源用第 2 版的格式，逐條記卡片代號；已存情境的編輯來源沒有讀回到條件上時原樣送回。
    else if (hasCommonMarker(s) || s.__restoredOrigins || isDraft) {
      wire.editorOrigins = { version: 2,
        nameIsAutomatic: !s.__nameDirty, rationaleIsAutomatic: !s.__rationaleDirty, groups: originGroups.map(function (g) {
        return { cards: g.conditions.map(function (r) { return r[COMMON_CARD_KEY] || null; }) };
      }) };
    }
    wire.groups.forEach(function (group) {
      group.conditions = group.conditions.map(nestedWireRule);
    });
    if (source) { wire.source = source; }
    return wire;
  }

  function nestedWireRule(rule) {
    var clean = {};
    Object.keys(rule).forEach(function (key) { if (key.indexOf('__') !== 0) { clean[key] = rule[key]; } });
    if (clean.conditions) { clean.conditions = clean.conditions.map(nestedWireRule); }
    if (clean.type === 'fieldValue') { return Ui.FilterValues.wire(clean, Store.getState()); }
    return clean;
  }

  // 群組組合器：群組內規則 join 的一致值，任一規則為 OR 就是 OR，否則是 AND；空群組用 AND。
  // 設定組合器就是把群組內每條規則的 join 設成同一個值，後端逐條依 join 評估，結果相同
  // （全部 AND 是符合全部條件，全部 OR 是符合任一條件）。
  function groupCombinator(group) {
    var rules = group.conditions.length > 1 ? group.conditions.slice(1) : group.conditions;
    return rules.some(function (r) { return effectiveConditionJoin(r) === 'OR'; }) ? 'OR' : 'AND';
  }

  // 有效組間運算子（單一情境層運算子的唯一推導點）：段控顯示、read-back、新組繼承、wire normalize
  // 四處一律讀這裡，保證「畫面顯示＝實際送出」。取值：第二組存在時取其 join（後端 left-fold
  // 下第一組的 join 沒有左運算元、不參與語意），否則預設 'OR'——與段控首次出現（加到第二組）時的
  // 顯示一致。
  function scenarioJoin(draft) {
    var groups = draft.groups || [];
    return groups[1] ? effectiveConditionJoin(groups[1]) : 'OR';
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
      var hasConditions = filter.draft.groups.some(function (group) { return group.conditions.length > 0; });
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
    syncViewState(state); // 案件切換／已存清單替換時對齊檢視狀態（先於任何讀取 viewState 的 HTML 生成）
    FilterStep.shownContainer = container;

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
          paneButton('filter', '篩選與檢視') + paneButton('saved', '已儲存情境（' + saved.length + '）') + '</div>' +
        '<section data-filter-pane="filter" id="filter-pane-filter" role="tabpanel" aria-labelledby="filter-tab-filter" data-motion-pane data-motion-key="filter-pane-filter"' + (FilterStep.viewState.workspacePane === 'filter' ? '' : ' hidden') + '>' +
          '<div class="filter-workbench"><aside class="filter-entry" tabindex="-1" aria-label="加入篩選條件">' +
            // 多組時「加入到第 N 組」和條件總覽的標題排在同一列。
            '<div class="filter-entry-heading"><h3 class="filter-entry-title">條件總覽</h3>' + conditionTargetHtml(draft) + '</div>' +
            '<div class="filter-source-tabs" role="tablist" aria-label="條件來源">' + sourceButton('common', '常用篩選條件') + sourceButton('prescreen', '預篩選') +
              sourceButton('field', '欄位條件') + '</div>' +
            '<div class="filter-entry-scroll" data-preserve-scroll="filter-condition-catalog">' +
            '<div' + conditionPaneAttributes('common') + '>' + commonPickerHtml(draft) + '</div>' +
            '<div' + conditionPaneAttributes('prescreen') + '>' + prescreenPickerHtml(draft) + '</div>' +
            '<div' + conditionPaneAttributes('field') + '>' + fieldPickerHtml() + '</div>' +
          '</div></aside><div class="filter-current">' + scenarioBuilderHtml(draft) + previewPaneHtml(state.filter.preview) + '</div></div>' +
        '</section>' +
        '<section data-filter-pane="saved" id="filter-pane-saved" role="tabpanel" aria-labelledby="filter-tab-saved" data-motion-pane data-motion-key="filter-pane-saved"' + (FilterStep.viewState.workspacePane === 'saved' ? '' : ' hidden') + '>' +
          // 在這個分頁出現的儲存提示（例如選了「儲存並編輯」）寫在這裡，句子和儲存鈕上方那一個相同（見 drawSaveNotice）。
          '<p class="form-notice" data-bind="scenario-notice-saved-pane" data-collapsible role="alert" hidden></p>' +
          savedScenariosHtml(state) + highRiskSummaryHtml(state) + criteriaReportHtml(state) + '</section>' +
        Ui.stepFooterHtml(state) + '</div>';

    bind(container);
    restoreViewState(container);
  }

  // 條件在畫面上的位置：第幾組第幾條；子條件組或傳票條件內的子條件再寫「的第幾項」。
  // 組號與條件組標頭「第 N 組」相同（草稿順序），條件號是組內順序。
  function conditionPositionText(gi, path) {
    var text = '第 ' + (gi + 1) + ' 組第 ' + (path[0] + 1) + ' 條';
    for (var i = 1; i < path.length; i++) { text += '的第 ' + (path[i] + 1) + ' 項'; }
    return text;
  }

  // 單一條件在畫面上就能看出的缺值或錯誤（驗證以後端為準）。回傳條件名稱與原因，沒有問題回 null。
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
    }
    if (!message) { return null; }
    if (label && message.indexOf(label) === 0) { message = message.slice(label.length); }
    return { label: label, message: message };
  }

  // 依畫面順序列出草稿中每一條有問題的條件，含子條件組與傳票條件內的子條件。
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
        if (rule.type === 'group' || rule.type === 'voucher') { visit(rule.conditions, gi, at); }
      });
    }
    (draft.groups || []).forEach(function (group, gi) { visit(group.conditions, gi, []); });
    return found;
  }

  // 某條條件要填值的輸入欄：先找值或日期的輸入欄，找不到再退回該列第一個可用控制項。
  function ruleValueControl(gi, path) {
    var row = document.querySelector('.filter-workspace .rule-row[data-gi="' + gi + '"][data-ri="' + path.join('.') + '"]');
    if (!row || !row.getClientRects().length) { return null; }
    var visible = function (control) { return control.getClientRects().length > 0; };
    var value = Array.from(row.querySelectorAll('[data-date-input], [data-value-key="values"], [data-value-key="daysOfMonth"], ' +
      '[data-value-key="value"], [data-value-key="from"], [data-value-key="to"]'))
      .filter(function (control) { return !control.disabled && control.closest('.rule-row') === row; }).find(visible);
    if (value) { return value; }
    return Array.from(row.querySelectorAll('.rule-row__controls input:not([disabled]), .rule-row__controls select:not([disabled]), .rule-row__controls textarea:not([disabled]), .rule-row__controls button:not([disabled])'))
      .filter(function (control) { return control.closest('.rule-row') === row; }).find(visible) || null;
  }

  // 預覽或儲存前還缺什麼，寫成提示框的句子；沒有缺項、名稱也沒有重複時 text 是空字串。驗證以後端為準。
  // 名稱與動機不必填：留白時後端補上依條件自動帶入的文字。
  function incompleteCheck(draft, includeSavedScenarios, skipDuplicateName) {
    var noCondition = (draft.groups.length === 0 ||
      draft.groups.every(function (g) { return g.conditions.length === 0; }));

    // 每條缺值都寫出第幾組第幾條與條件名稱，使用者不必逐條找。
    var problems = draftRuleProblems(draft).map(function (item) { return item.text; });
    if (noCondition) { problems.push('至少一條篩選條件'); }

    // 名稱不可和其他已儲存情境相同；是否重複以後端為準，這裡先在名稱欄旁說明，不必送出才知道。
    // 「另存為新情境」時名稱會另附「（副本）」，不在這裡比對。
    var duplicate = includeSavedScenarios && !skipDuplicateName ? duplicateScenarioName(draft) : '';
    var duplicateText = duplicate ? '已有同名情境「' + duplicate + '」，請改用其他名稱。' : '';
    return {
      text: (problems.length ? '尚需補齊：' + problems.join('、') + (duplicate ? '。' : '') : '') + duplicateText,
      duplicateText: duplicateText
    };
  }

  /* ---- 送出之後要留在畫面上的東西：條件列的錯誤，以及儲存鈕上方的提示 ----
     兩者都記在 FilterStep.viewState（ruleErrors、saveNotice），畫面由 drawRuleErrors 與 drawSaveNotice 照記下的內容畫上去：
     每次重畫之後畫一次，內容有變動時再畫一次。後端的回應回來時，畫的對象是這一步現在畫在上面的那一塊畫面
     （FilterStep.shownContainer），不是送出當時的那一塊：等待回應的期間中央面板可能已經重畫，原本那一塊不在畫面上了。 */

  // 一條條件送給後端的樣子。後端回報的錯誤說的是這個樣子，條件改了就不再適用。
  function sentRuleKey(rule) { return JSON.stringify(nestedWireRule(rule)); }

  // 把後端 invalid_scenario 的 details（第幾組、第幾條、原因）記到草稿的條件上，回傳記下幾則；上一次記的先清掉。
  // 沒有 details 的錯誤不記，維持 Ui.run 的整段訊息。這只是呈現，判定以後端為準。
  // 後端的組號是送出後的順序；送出時略過空組，所以先換回草稿裡有條件的組。
  // targetPosition 是這次儲存的情境在清單上的位置；細節標明屬於別的情境時不記在目前草稿上。
  function recordRuleErrors(error, targetPosition) {
    var errors = [];
    if (error && Array.isArray(error.details)) {
      var sentGroups = Store.getState().filter.draft.groups.filter(function (group) { return (group.conditions || []).length; });
      error.details.forEach(function (detail) {
        if (!detail || typeof detail.group !== 'number' || typeof detail.rule !== 'number') { return; }
        if (!belongsToTarget(detail, targetPosition)) { return; }
        var group = sentGroups[detail.group - 1];
        var rule = group ? group.conditions[detail.rule - 1] : null;
        if (!rule) { return; }
        errors.push({ rule: rule, message: detail.message, sent: sentRuleKey(rule) });
      });
    }
    FilterStep.viewState.ruleErrors = errors;
    return errors.length;
  }

  // 現在仍適用的條件錯誤：條件還在草稿裡，內容也和送出時相同。不再適用的從記錄拿掉，之後不會再出現。
  function currentRuleErrors() {
    var groups = Store.getState().filter.draft.groups || [];
    FilterStep.viewState.ruleErrors = FilterStep.viewState.ruleErrors.filter(function (item) {
      return groups.some(function (group) { return (group.conditions || []).indexOf(item.rule) >= 0; }) &&
        sentRuleKey(item.rule) === item.sent;
    });
    return FilterStep.viewState.ruleErrors;
  }

  // 把記下的條件錯誤畫到條件列：有錯的列標紅、在列的下面寫原因，已經不適用的拿掉。回傳第一個標紅的列，沒有時是 null。
  // animate：錯誤剛出現或剛收起時，說明長出來、收回去（出現與消失都要有動態）；重畫之後照原樣畫回去時不重播。
  function drawRuleErrors(container, animate) {
    if (!container) { return null; }
    var groups = Store.getState().filter.draft.groups || [];
    var wanted = [];
    currentRuleErrors().forEach(function (item) {
      var gi = groups.findIndex(function (group) { return (group.conditions || []).indexOf(item.rule) >= 0; });
      var row = container.querySelector('.rule-row[data-gi="' + gi + '"][data-ri="' + groups[gi].conditions.indexOf(item.rule) + '"]');
      if (!row) { return; }
      var entry = wanted.filter(function (found) { return found.row === row; })[0];
      if (!entry) { entry = { row: row, messages: [], drawn: false }; wanted.push(entry); }
      entry.messages.push(item.message);
    });
    container.querySelectorAll('.rule-row--invalid').forEach(function (row) {
      // 正在收起的舊畫面複本不動，它會自己淡掉。
      if (row.closest('[data-motion-leaving]')) { return; }
      var notes = Array.prototype.filter.call(row.querySelectorAll('[data-rule-error]:not([data-motion-folding])'),
        function (note) { return note.parentNode === row; });
      var entry = wanted.filter(function (found) { return found.row === row; })[0];
      if (entry && notes.map(function (note) { return note.textContent; }).join('\n') === entry.messages.join('\n')) {
        entry.drawn = true;
        return;
      }
      row.classList.remove('rule-row--invalid');
      notes.forEach(function (old) {
        if (animate && global.JetMotion && typeof global.JetMotion.fold === 'function') { global.JetMotion.fold(old, function () { old.remove(); }); } else { old.remove(); }
      });
    });
    wanted.forEach(function (entry) {
      if (entry.drawn) { return; }
      entry.row.classList.add('rule-row--invalid');
      entry.messages.forEach(function (message) {
        var note = document.createElement('p');
        note.className = 'rule-row__error'; note.setAttribute('data-rule-error', ''); note.setAttribute('role', 'alert');
        note.textContent = message;
        entry.row.appendChild(note);
        if (animate && global.JetMotion && typeof global.JetMotion.enter === 'function') { global.JetMotion.enter(note); }
      });
    });
    return wanted.length ? wanted[0].row : null;
  }

  // 儲存鈕上方的提示框一次只寫一種提示，記在 viewState.saveNotice（kind、text、warning、pane）。每一種有自己的收起時機：
  //   incomplete  按下預覽或儲存時還有要補的，或名稱重複；同一份檢查通過就收起。
  //   listChanged 這次沒有送出：編輯中的情境所在的清單已經換掉；草稿不再接在舊清單上就收起。
  //   listFull    這次沒有送出：已經存滿；清單有空位就收起。
  //   failed      後端沒有存成，原因又對不到條件列；原因說的是送出的那一份草稿，草稿改了就收起。
  // 再按一次預覽或儲存、或換成另一份草稿時，不論哪一種都先收起。
  // pane 是提示出現時使用者所在的分頁：在「已儲存情境」分頁出現的提示（例如選了「儲存並編輯」），
  // 同一句也寫在那個分頁的最上面，不只寫在另一個分頁的儲存鈕上方。
  function raiseSaveNotice(notice) {
    notice.pane = FilterStep.viewState.workspacePane;
    FilterStep.viewState.saveNotice = notice;
  }

  // 現在仍適用的提示；情況已經不在的從記錄拿掉。rewrite 為真時，尚需補齊的句子照目前還缺的重寫。
  function currentSaveNotice(rewrite) {
    var notice = FilterStep.viewState.saveNotice;
    if (!notice) { return null; }
    var state = Store.getState(), draft = state.filter.draft, saved = state.filter.savedScenarios;
    var holds;
    if (notice.kind === 'incomplete') {
      var text = incompleteCheck(draft, notice.includeSaved, notice.skipDuplicate).text;
      holds = !!text;
      if (holds && rewrite) { notice.text = text; }
    } else if (notice.kind === 'listChanged') {
      holds = typeof draft.__editingIndex === 'number' && draft.__editingSavedRef !== saved;
    } else if (notice.kind === 'listFull') {
      holds = saved.length >= Ui.FILTER_MAX_SAVED_SCENARIOS;
    } else {
      holds = draftContentKey(draft) === notice.sent;
    }
    if (!holds) { FilterStep.viewState.saveNotice = null; }
    return FilterStep.viewState.saveNotice;
  }

  // 把記下的提示畫到提示框：儲存鈕上方那一個，以及「已儲存情境」分頁最上面那一個（提示是在那個分頁出現時）。
  // 提示收起時字留著，跟著一起收回去，不會先變成一個空框。
  // typing：使用者正在輸入時只收起已經不適用的提示，不改寫句子，免得打字途中跳出新的缺項。
  function drawSaveNotice(container, typing) {
    if (!container) { return; }
    var notice = currentSaveNotice(!typing);
    [[container.querySelector('[data-bind="scenario-notice"]'), !!notice],
      [container.querySelector('[data-bind="scenario-notice-saved-pane"]'), !!notice && notice.pane === 'saved']].forEach(function (target) {
      var box = target[0];
      if (!box) { return; }
      if (target[1]) {
        box.textContent = notice.text;
        // 清單已變更與已存滿屬於「需要注意」，用琥珀色；尚需補齊與儲存失敗是錯誤，用紅色。
        if (notice.warning) { box.classList.add('form-notice--warning'); } else { box.classList.remove('form-notice--warning'); }
      }
      box.hidden = !target[1];
    });
  }

  // 預覽／保存前的前端引導：條件未補齊或名稱重複時，於欄位旁（紅框＋紅字）
  // 與按鈕上方（form-notice）就近提示並不送出。純呈現——是否有效以後端的 invalid_scenario 為準。
  function scenarioGate(container, includeSavedScenarios, skipDuplicateName) {
    var draft = Store.getState().filter.draft;
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
    var check = incompleteCheck(draft, includeSavedScenarios, skipDuplicateName);

    // 已經顯示的錯誤說明直接換字，不先收起再長出來；補齊的才收回去。
    if (check.duplicateText) { setFieldError(nameInput, check.duplicateText); } else { clearFieldError(nameInput); }
    clearFieldError(rationaleInput);

    if (check.text) {
      raiseSaveNotice({ kind: 'incomplete', text: check.text, warning: false,
        includeSaved: !!includeSavedScenarios, skipDuplicate: !!skipDuplicateName });
    } else if (FilterStep.viewState.saveNotice && FilterStep.viewState.saveNotice.kind === 'incomplete') {
      // 檢查通過只收起尚需補齊；上一次沒有送出或儲存失敗的提示，等這一次真的開始送出才收起。
      FilterStep.viewState.saveNotice = null;
    }
    drawSaveNotice(container);
    if (!check.text) { return true; }
    revealSaveNotice(container);
    // 名稱重複時聚焦名稱；只缺條件時不搶焦點。
    if (check.duplicateText) { nameInput.focus(); }
    return false;
  }

  // 提示剛出現在「已儲存情境」分頁時，把它捲到看得見的地方；儲存鈕上方那一個就在按下的按鈕旁邊，不必捲。
  function revealSaveNotice(container) {
    var notice = FilterStep.viewState.saveNotice;
    var box = container && notice && notice.pane === 'saved'
      ? container.querySelector('[data-bind="scenario-notice-saved-pane"]') : null;
    if (box && typeof box.scrollIntoView === 'function') { box.scrollIntoView({ block: 'nearest', behavior: smoothScroll() }); }
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

  // 使用者輸入時放寬（不在打字途中跳出新錯誤）——只清除已補齊欄位的錯誤；
  // 儲存鈕上方的提示與條件列的錯誤，各自的情況不在了才收起（見 drawSaveNotice、drawRuleErrors）。不在此新增任何錯誤。
  function softRefreshGate(container) {
    var draft = Store.getState().filter.draft;
    var feedback = container.querySelector('[data-bind="save-feedback"]');
    var feedbackText = savedFeedbackText(draft);
    if (feedback && feedbackText) { feedback.innerHTML = feedbackText; feedback.hidden = false; }
    else if (feedback) { feedback.hidden = true; }
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');

    var duplicate = duplicateScenarioName(draft);
    if (!duplicate) { clearFieldError(nameInput); }
    clearFieldError(rationaleInput);

    drawSaveNotice(container, true);
    drawRuleErrors(container, true);
  }

  // 規則值編輯後即時刷新讀回：值編輯走 patchFilterCondition（只 notify、不重建面板，以保住輸入
  // 焦點），所以讀回（整段算好的 HTML）不會自己更新。這裡只抽換 .scenario-readback 這一段，不碰
  // 任何輸入框，焦點不受影響（讀回是條件清單之後的獨立節點）。
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

  // 值編輯後把預覽面板軟更新為「已失效」空狀態：patchFilterCondition 已把 Store 的預覽清掉（值編輯改變
  // 命中集合），但它只 notify 不重繪（保住輸入焦點），畫面上的舊命中數不會自己消失——這裡只抽換
  // 預覽面板的內容區，不碰任何輸入框。已是空狀態時直接返回（冪等，連續打字不重複動 DOM、不抖動）。
  function softExpirePreviewPane(container) {
    var body = Ui.findLive(container, '[data-bind="preview-pane-body"]');
    if (!body || body.getAttribute('data-empty') === '1') { return; }
    fillSmoothly(body, function () { body.innerHTML = '<p class="empty-state">條件已變更。按「查看符合的傳票」更新結果。</p>'; });
    body.setAttribute('data-empty', '1');
  }

  // 送出：另存為新情境、名稱與動機、預覽、儲存與就地標錯。
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

    // 真的開始送出時，上一次留下的條件錯誤與提示先收起。回傳收起前的內容：這一次被取消或另一項作業還在進行時，
    // 畫面要回到送出前的樣子，交給 restoreSubmissionProblems 放回去。
    function clearSubmissionProblems() {
      var earlier = { ruleErrors: FilterStep.viewState.ruleErrors, saveNotice: FilterStep.viewState.saveNotice };
      FilterStep.viewState.ruleErrors = [];
      FilterStep.viewState.saveNotice = null;
      drawSubmissionProblems();
      return earlier;
    }
    function restoreSubmissionProblems(earlier) {
      FilterStep.viewState.ruleErrors = earlier.ruleErrors;
      FilterStep.viewState.saveNotice = earlier.saveNotice;
      drawSubmissionProblems();
    }
    // 條件錯誤或提示有變動時畫到現在的畫面上，回傳第一個標紅的條件列。
    function drawSubmissionProblems() {
      drawSaveNotice(FilterStep.shownContainer);
      return drawRuleErrors(FilterStep.shownContainer, true);
    }
    // 後端剛回報條件錯誤：畫上去，並把第一個標紅的列捲到看得見的地方。
    function revealRuleErrors() {
      var first = drawSubmissionProblems();
      if (first) { first.scrollIntoView({ block: 'center', behavior: smoothScroll() }); }
    }

    container.querySelector('[data-action="preview-scenario"]').addEventListener('click', function () {
      if (!scenarioGate(container, false)) { return; }
      // 預覽失敗時，只有把原因標到條件列才算就地顯示；沒有標到時在步驟上方顯示提示框。
      var previewMarked = 0;
      Ui.run('查看符合的傳票', function () {
        previewMarked = 0;
        var earlierProblems = clearSubmissionProblems();
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
        }).catch(function (error) {
          if (!acceptResponse()) { return; }
          // 取消與另一項作業還在進行不是預覽失敗：送出前畫面上的錯誤與提示照原樣放回來。
          if (Ui.isCancelledOrInProgress(error)) { restoreSubmissionProblems(earlierProblems); throw error; }
          previewMarked = recordRuleErrors(error);
          revealRuleErrors();
          throw error;
        });
      }, { inlineError: function () { return previewMarked > 0; }, completionMessage: function (scenario) {
        // 訊息的分錄數用和預覽摘要同一個 summaryRowCount。
        return scenario ? '情境預覽：符合條件 ' + Ui.countText(scenario.summaryRowCount) + ' 筆分錄，' + Ui.countText(scenario.voucherCount) + ' 張傳票。' : '';
      } });
    });

    // 儲存沒有送出時，原因寫在儲存按鈕上方，不只留在訊息紀錄；訊息面板與提示用同一句。
    // 清單已變更與已存滿屬於「需要注意」，用琥珀色。
    function blockSave(kind, text) {
      Store.addMessage(text, 'warn');
      raiseSaveNotice({ kind: kind, text: text, warning: true });
      drawSubmissionProblems();
      revealSaveNotice(FilterStep.shownContainer);
    }
    // 前端擋下、沒有送出的儲存不記「儲存篩選情境完成」。
    var SAVE_NOT_SENT = { notSent: true };

    // 回傳儲存作業的 Promise，讓「儲存並編輯」這類對話框等儲存結束再決定要不要繼續。
    // 只送這一個情境，儲存情境才不會卡住：其他已存情境由後端照資料庫原樣保留，
    // 開案時無法套用目前規則的情境不會擋住這次儲存。
    function saveScenario(asCopy) {
      syncSuggestedMetadata(container);
      if (!scenarioGate(container, true, asCopy)) { return Promise.resolve(SAVE_NOT_SENT); }
      // 儲存失敗時，原因有沒有寫在使用者現在看得到的地方；沒有的話改在步驟上方顯示提示框。
      var failureShown = false;
      return Ui.run('儲存篩選情境', function () {
        failureShown = false;
        var earlierProblems = clearSubmissionProblems();
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
            blockSave('listChanged', '已儲存的情境清單已變更，請重新選擇要編輯的情境；目前的草稿仍保留。');
            return Promise.resolve(SAVE_NOT_SENT);
          }
          scenarios[editingIndex] = authored;
          position = editingIndex + 1;
        } else { scenarios.push(authored); }
        if (scenarios.length > Ui.FILTER_MAX_SAVED_SCENARIOS) {
          blockSave('listFull', '最多可儲存 ' + Ui.FILTER_MAX_SAVED_SCENARIOS + ' 個篩選情境，請先移除不需要的情境。');
          return Promise.resolve(SAVE_NOT_SENT);
        }
        // 新增或另存時加在最後，和後端的算法相同；修改時後端用 expectedName 確認畫面清單和資料庫是同一個情境。
        var targetPosition = position === null ? current.savedScenarios.length + 1 : position;
        var payload = { populationScope: populationScope, position: position, scenario: authored };
        if (position !== null) { payload.expectedName = current.savedScenarios[editingIndex].name; }
        var saveSnapshot = filterCommitSnapshot(commitState);
        return global.JetApi.filterScenarioSave(payload).then(function (data) {
          // 完成句由 Ui.run 接上耗時後記一則，成功時只留一則；回應已過時就不記。
          return completeScenarioSave(saveSnapshot, data, data.position - 1, authored) ? data : null;
        }).catch(function (error) {
          if (!acceptsFilterCommit(saveSnapshot)) { return; }
          var sameDraft = Store.getState().filter.draft === saveSnapshot.draft && Store.getFilterDraftRev() === saveSnapshot.revision;
          if (Ui.isCancelledOrInProgress(error)) {
            // 取消與另一項作業還在進行不是儲存失敗，不標條件列，也不在儲存按鈕上方寫原因；送出前畫面上的錯誤與提示照原樣放回來。
            if (sameDraft) { restoreSubmissionProblems(earlierProblems); }
          } else if (sameDraft) {
            // 能對到條件列的錯誤就地標紅；重名這類沒有位置的錯誤寫在儲存按鈕上方。
            if (recordRuleErrors(error, targetPosition)) {
              // 條件列只畫在「篩選與檢視」分頁；使用者在另一個分頁時看不到，原因改在步驟上方顯示。
              failureShown = FilterStep.viewState.workspacePane === 'filter';
              revealRuleErrors();
            } else {
              raiseSaveNotice({ kind: 'failed', text: saveFailureText(error, targetPosition), warning: false,
                sent: draftContentKey(saveSnapshot.draft) });
              failureShown = true;
              drawSubmissionProblems();
              revealSaveNotice(FilterStep.shownContainer);
            }
          }
          throw error;
        });
      }, {
        // 儲存失敗的原因已經寫在條件列或儲存按鈕上方時，不再另外在步驟上方顯示；條件在等待期間改過、原因沒有地方寫時才顯示。
        inlineError: function () { return failureShown; },
        completionMessage: function (data) {
          return data && data !== SAVE_NOT_SENT ? '已儲存篩選情境（' + data.savedCount + ' / 10）。' : '';
        }
      });
    }
    container.querySelector('[data-action="save-scenario"]').addEventListener('click', function () { saveScenario(false); });
    FilterStep.saveDraftForReplacement = function () { return saveScenario(false); };

  }

  // 事件綁定分到五個檔案，各綁自己畫的那一塊。
  function bind(container) {
    Ui.bindReports(container);
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
    // 重畫之後，照記下的內容把條件列的錯誤與儲存鈕上方的提示畫回去。
    drawRuleErrors(container, false);
    drawSaveNotice(container);
  }

  // 掛到共用的 FilterStep，供其他檔案與測試取用。
  FilterStep.toWireScenario = toWireScenario;
  FilterStep.projectWireScenario = projectWireScenario;
  FilterStep.nestedWireRule = nestedWireRule;
  FilterStep.groupCombinator = groupCombinator;
  FilterStep.scenarioJoin = scenarioJoin;
  FilterStep.toWireDraft = toWireDraft;
  FilterStep.previewPaneHtml = previewPaneHtml;
  FilterStep.render = render;
  FilterStep.conditionPositionText = conditionPositionText;
  FilterStep.ruleProblem = ruleProblem;
  FilterStep.draftRuleProblems = draftRuleProblems;
  FilterStep.ruleValueControl = ruleValueControl;
  FilterStep.incompleteCheck = incompleteCheck;
  FilterStep.sentRuleKey = sentRuleKey;
  FilterStep.recordRuleErrors = recordRuleErrors;
  FilterStep.currentRuleErrors = currentRuleErrors;
  FilterStep.drawRuleErrors = drawRuleErrors;
  FilterStep.raiseSaveNotice = raiseSaveNotice;
  FilterStep.currentSaveNotice = currentSaveNotice;
  FilterStep.drawSaveNotice = drawSaveNotice;
  FilterStep.revealSaveNotice = revealSaveNotice;
  FilterStep.scenarioGate = scenarioGate;
  FilterStep.duplicateScenarioName = duplicateScenarioName;
  FilterStep.softRefreshGate = softRefreshGate;
  FilterStep.softRefreshReadback = softRefreshReadback;
  FilterStep.softExpirePreviewPane = softExpirePreviewPane;
  FilterStep.bindSubmission = bindSubmission;
  FilterStep.bind = bind;

  Ui.registerStep('filter', render);
})(window);
