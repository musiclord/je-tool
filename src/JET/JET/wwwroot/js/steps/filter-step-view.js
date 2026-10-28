/*
  第五步：進階條件篩選 —— 檢視狀態與資料世代。
  純檢視狀態（頁籤、摺疊、選取中的條件）、重繪動態用的識別、母體範圍、欄位錯誤提示，
  以及案件、已存清單、資料或結果版本改變時對齊檢視狀態。這些狀態只放畫面上，不寫進草稿，也不送出。
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
  var ruleValueControl = forward('ruleValueControl');
  var ensureScenarioPreview = forward('ensureScenarioPreview');
  var ensureMatrixContent = forward('ensureMatrixContent');

  FilterStep.selectedRule = null;

  var categoryPickerViews = new WeakMap();

  function categoryPickerView(rule, key) {
    if (!categoryPickerViews.has(rule)) { categoryPickerViews.set(rule, {}); }
    var views = categoryPickerViews.get(rule);
    if (!views[key]) { views[key] = { search: '', collapsed: {} }; }
    return views[key];
  }

  // app.js 的 JetMotion 用的識別：同一個條件或條件組物件在每次重繪都用同一個 data-motion-key，
  // 加入的長出來、移除與清除的收起來。只放在畫面上，不寫進草稿，也不送出。
  // 還沒有條件組時畫的空白第 1 組，和之後真的建立的第 1 組是同一塊，所以建組時接手它的識別。
  var motionKeys = new WeakMap();

  var motionSerial = 0;

  var EMPTY_FIRST_GROUP = {};

  function motionKey(object, prefix) {
    if (!motionKeys.has(object)) { motionSerial += 1; motionKeys.set(object, prefix + '-' + motionSerial); }
    return motionKeys.get(object);
  }

  function groupMotionKey(group) {
    return motionKey(group || EMPTY_FIRST_GROUP, 'set');
  }

  // 換掉的物件仍是畫面上同一個東西時（清除條件組、換條件的對象），新的物件接手舊的識別。
  function keepMotionKey(previous, next) {
    if (previous && next && motionKeys.has(previous)) { motionKeys.set(next, motionKeys.get(previous)); }
  }

  // 頁籤底下的窗格原地切換（使用者 2026-10-12）：新的窗格從舊窗格的高度平順變成自己的高度，舊的那一份疊在底下淡掉，
  // 下面的內容跟著移動；裡面原本就打開的展開區不因為窗格重新出現而再長一次（app.js 的 JetMotion.swap）。
  // chosen 挑出要顯示的窗格；fill 在它顯示之後、量高度之前補上還沒畫的內容。
  function showOnly(panes, chosen, fill) {
    var list = Array.prototype.slice.call(panes).filter(Boolean);
    var to = list.filter(chosen)[0] || null;
    var from = list.filter(function (pane) { return pane !== to && !pane.hidden; })[0] || null;
    list.forEach(function (pane) { if (pane !== to && pane !== from) { pane.hidden = true; } });
    if (to && global.JetMotion && global.JetMotion.swap) { global.JetMotion.swap(from, to, fill); return; }
    if (from) { from.hidden = true; }
    if (to) { to.hidden = false; }
    if (fill) { fill(); }
  }

  // focusValue：起訖或門檻留空的條件，焦點直接移到要填的值。
  // 加入的條件正由標籤帶過去時，中欄跟著標籤平順捲到這一列（app.js 的 JetMotion），這裡不再瞬間捲過去，移焦點也不捲。
  function revealAddedRule(rule, focusValue) {
    FilterStep.selectedRule = rule;
    global.JetFocus.defer(function () {
      var draft = Store.getState().filter.draft;
      var gi = draft.groups.findIndex(function (group) { return group.rules.indexOf(rule) >= 0; });
      if (gi < 0) { return null; }
      var ri = draft.groups[gi].rules.indexOf(rule);
      var row = document.querySelector('.filter-workspace .rule-row[data-gi="' + gi + '"][data-ri="' + ri + '"]');
      if (!row || !row.getClientRects().length) { return null; }
      var carried = !!(global.JetMotion && global.JetMotion.carries && global.JetMotion.carries(row));
      if (!carried) { row.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' }); }
      var value = focusValue ? ruleValueControl(gi, [ri]) : null;
      var control = value || Array.from(row.querySelectorAll('.rule-row__controls input:not([disabled]), .rule-row__controls select:not([disabled]), .rule-row__controls textarea:not([disabled]), .rule-row__controls button:not([disabled])'))
        .find(function (control) { return control.getClientRects().length > 0; });
      if (carried && control) {
        global.JetFocus.move(control, { preventScroll: true });
        return null;
      }
      return control;
    });
  }

  var POPULATION_SCOPE_AUDIT = 'auditPeriod';

  function selectedPopulationScope() {
    return POPULATION_SCOPE_AUDIT;
  }

  function committedPopulationScope(state) {
    var resultRef = state.filterResultRef;
    if (!resultRef || !resultRef.revision) { return null; }
    return resultRef.populationScope === POPULATION_SCOPE_AUDIT
      ? POPULATION_SCOPE_AUDIT
      : null;
  }

  function populationScopeLabel() {
    return '查核期間';
  }

  /* ---- 欄位驗證提示（就近顯示在欄位旁，不依賴可收合的訊息欄） -------------
     錯誤訊息放在欄位旁邊，同時用紅框和紅字提示，不只靠顏色，讓使用者不必另外找訊息欄。
     這是純呈現輔助：標紅框、在欄位（form__row）下方插一行紅字；權威驗證仍在後端。
     原掛在 ui-core（JetUi），因全前端只有本步驟使用而搬入本檔。 */

  // 在欄位上加紅框並在其所在 form__row 下方顯示一行訊息。message 省略時沿用既有文字。
  function setFieldError(input, message) {
    if (!input) { return; }
    input.classList.add('form__input--error');
    input.setAttribute('aria-invalid', 'true');
    var row = input.parentNode;
    if (!row) { return; }
    // 正在收起的舊訊息不沿用；新的一行從欄位下方長出來，清掉時收回去（使用者 2026-10-13 要求出現與消失都有動態）。
    var err = row.querySelector('.field-error:not([data-motion-folding])');
    if (!err) {
      err = document.createElement('span');
      err.className = 'field-error';
      err.setAttribute('role', 'alert');
      if (message != null) { err.textContent = message; }
      row.appendChild(err);
      if (global.JetMotion && typeof global.JetMotion.enter === 'function') { global.JetMotion.enter(err); }
      return;
    }
    if (message != null) { err.textContent = message; }
  }

  // 清掉某欄位的錯誤狀態（紅框與訊息）。使用者修正欄位時即呼叫，避免「持續責備」。訊息收回欄位下方再拿掉。
  function clearFieldError(input) {
    if (!input) { return; }
    input.classList.remove('form__input--error');
    input.removeAttribute('aria-invalid');
    var row = input.parentNode;
    var err = row ? row.querySelector('.field-error:not([data-motion-folding])') : null;
    if (!err) { return; }
    var drop = function () { if (err.parentNode) { err.parentNode.removeChild(err); } };
    if (global.JetMotion && typeof global.JetMotion.fold === 'function') { global.JetMotion.fold(err, drop); } else { drop(); }
  }

  /* ============================================================================
     檢視狀態（module 層；絕不進 draft、絕不進 wire）。
     草稿的每次結構變更都走 setFilterDraft → bump 全重繪；展開狀態若只活在 DOM，使用者每加一條
     條件，挑選區就收回、已存情境詳情就收合並丟掉已載入的預覽——操作連續性全毀。因此把這些
     純檢視狀態放到重繪之外。放 module 層而非 Store：比照匯入精靈「模組區域狀態」的既有慣例
     （見 state.js touch() 的註解），Store 不需要知道單一步驟的展開細節；生命週期由本模組在每次
     render 時自行對齊（syncViewState）。
       checkCategory    —— 「常用查核條件」目前展開的分類（一次只展開一類；null 表示全部收起）。
       examplesOpen     —— 「從範例開始」清單是否展開。
       examplePick      —— 正在選欄位的範例（範例需要文字型攸關資料元素欄位且有兩個以上可選時）。
       openScenarios    —— 已存情境詳情展開狀態（index → true）。
       scenarioPreviews —— 已存情境惰性命中預覽快取（index → filter.preview 回傳的 scenario 物件）。
                           草稿編輯不影響已存情境的命中（已存定義只在 commit 時變），跨草稿編輯保留
                           快取是安全的。
       matrixOpen / matrixData —— 高風險條件矩陣展開狀態與情境摘要（tagMatrixScenarios）快取。
       cleanDraftKey    —— 換成目前這份草稿後，第一次畫好時的內容；之後內容不同才算有還沒存的修改。
                           換草稿時清成 null，下一次 bind 在自動名稱與 KCT 身分收斂後補記。
     重置時機（四個訊號、範圍遞減）：
       1. 專案切換或同案重新載入（project 參照變）→ 全重置。
       2. 已存清單被替換（savedScenarios 參照變＝commit 成功、移除情境、resume、resetWorkflow）
          → 重置詳情與矩陣——index 對位與命中都可能已變。
       3. 已保存 resultRef 換版（revision／scope 變）→ 只作廢資料快取，保留展開狀態。
       4. 底層資料世代變（GL／科目配對／分類／授權清單／行事曆的匯入狀態，加上後端回報命中失效的次數）
          → 只作廢資料快取：已存定義沒變、展開狀態保留，但命中是舊資料算的，restore 會就地重抓。
          哪些上游修改會作廢命中由後端回應的失效清單決定，TB 和 GL 相同（使用者 2026-10-07 裁定），
          前端收到後 filterResultGeneration 遞增，就落在這個訊號裡。
     ============================================================================ */
  function freshViewState() {
    return {
      workspacePane: 'filter',
      conditionSource: 'kct',
      checkCategory: Ui.FILTER_COMMON_CHECK_GROUPS[0].key,
      examplesOpen: false,
      examplePick: null,
      lastSavedName: null,
      savedDraftKey: null,
      disclosures: {},
      matrixView: 'scenarios',
      openScenarios: {},
      scenarioPreviews: {},
      matrixOpen: false,
      matrixData: null,
      pendingRemovalIndex: null,
      cleanDraftKey: null
    };
  }

  FilterStep.viewState = freshViewState();

  var viewProjectKey = null;

  var viewProjectRef = null;

  var viewSavedRef = null;

  var viewDraftRef = null;

  var viewDataGen = null;

  var viewResultKey = null;

  var draftResponseGuard = Ui.createLatestResponseGuard();

  var matrixResponseGuard = Ui.createLatestResponseGuard();

  var scenarioResponseGuards = {};

  function scenarioResponseGuard(index) {
    if (!scenarioResponseGuards[index]) {
      scenarioResponseGuards[index] = Ui.createLatestResponseGuard();
    }
    return scenarioResponseGuards[index];
  }

  function invalidateScenarioResponseGuards() {
    Object.keys(scenarioResponseGuards).forEach(function (key) {
      scenarioResponseGuards[key].invalidate();
    });
    scenarioResponseGuards = {};
  }

  // 底層資料世代追 GL／科目配對／分類／授權名單／行事曆的匯入狀態，加上後端回報命中失效的次數；
  // 上游修改（含 TB）要不要作廢命中，由後端回應的失效清單決定，前端不自己判斷。草稿編輯完全不碰這些參照。
  function dataGeneration(state) {
    var imp = state.importState;
    // filterResultGeneration：後端回報篩選命中失效時遞增，例如修改準備日（V7）。
    return [imp.gl, imp.accountMapping, imp.authorizedPreparer, imp.calendar,
      state.mapping.gl.committed, state.taxonomy, state.filterResultGeneration];
  }

  function resultKey(state) {
    var resultRef = state.filterResultRef;
    return resultRef && resultRef.revision
      ? resultRef.revision + '|' + (resultRef.populationScope || '')
      : '';
  }

  function sameGeneration(a, b) {
    if (!a || !b || a.length !== b.length) { return false; }
    for (var i = 0; i < a.length; i++) {
      if (a[i] !== b[i]) { return false; }
    }
    return true;
  }

  function syncViewState(state) {
    var key = state.project ? state.project.projectId : null;
    var gen = dataGeneration(state);
    if (key !== viewProjectKey || state.project !== viewProjectRef) {
      draftResponseGuard.invalidate();
      invalidateScenarioResponseGuards();
      matrixResponseGuard.invalidate();
      viewProjectKey = key;
      viewProjectRef = state.project;
      FilterStep.selectedRule = null;
      viewSavedRef = state.filter.savedScenarios;
      viewDraftRef = state.filter.draft;
      viewDataGen = gen;
      viewResultKey = resultKey(state);
      FilterStep.viewState = freshViewState();
      return;
    }
    // 換成另一份草稿後，等下一次畫好再記下它沒有修改時的內容。
    if (state.filter.draft !== viewDraftRef) {
      viewDraftRef = state.filter.draft;
      FilterStep.viewState.cleanDraftKey = null;
    }
    if (state.filter.savedScenarios !== viewSavedRef) {
      invalidateScenarioResponseGuards();
      matrixResponseGuard.invalidate();
      viewSavedRef = state.filter.savedScenarios;
      FilterStep.viewState.openScenarios = {};
      FilterStep.viewState.scenarioPreviews = {};
      FilterStep.viewState.matrixOpen = false;
      FilterStep.viewState.matrixData = null;
      FilterStep.viewState.pendingRemovalIndex = null;
      // 「已儲存「A」」只在 A 仍是已存情境時顯示；上游修改清掉情境或 A 被刪除後就收起（使用者 2026-10-15 裁定）。
      if (FilterStep.viewState.lastSavedName && !state.filter.savedScenarios.some(function (scenario) { return scenario.name === FilterStep.viewState.lastSavedName; })) {
        FilterStep.viewState.lastSavedName = null;
      }
    }
    var nextResultKey = resultKey(state);
    if (nextResultKey !== viewResultKey) {
      invalidateScenarioResponseGuards();
      matrixResponseGuard.invalidate();
      viewResultKey = nextResultKey;
      FilterStep.viewState.scenarioPreviews = {};
      FilterStep.viewState.matrixData = null;
    }
    if (!sameGeneration(gen, viewDataGen)) {
      invalidateScenarioResponseGuards();
      matrixResponseGuard.invalidate();
      viewDataGen = gen;
      FilterStep.viewState.scenarioPreviews = {};
      FilterStep.viewState.matrixData = null;
    }
  }

  // 一組條件還缺什麼前置條件，沒有缺項時是空字串。判斷與句子都在 ui-core.js 的規則需求表，它逐項鏡像後端（權威驗證在後端）；
  // 欄位按鈕、常用查核條件、範例、KCT 卡、預篩選分頁與條件卡片都讀這一個函式（使用者 2026-10-22 裁定第五步不能用 GL 沒有配對的欄位）。
  function requirementNote(rules) {
    return Ui.ruleRequirementNote(Store.getState(), rules);
  }

  // 一條條件本身還缺什麼，不含子條件；子條件在畫面上各有自己的卡片。
  function ownRequirementNote(rule) {
    return Ui.ruleRequirementNote(Store.getState(), [rule], true);
  }

  // 要拿掉的條件先輕微快速地晃一下再收起來（使用者 2026-10-13，app.js 的 JetMotion.removing）。
  // 移除鈕在條件列裡的由 app.js 的 REMOVAL_SOURCES 處理；按鈕不在要拿掉的條件裡時（例如再按一次 KCT 卡片），在這裡說明是哪幾條。
  function announceRemoval(rules) {
    if (!rules.length || !global.JetMotion || typeof global.JetMotion.removing !== 'function') { return; }
    global.JetMotion.removing({ keys: rules.map(function (rule) { return motionKey(rule, 'rule'); }) });
  }

  // 清空整個條件組合（取消編輯、新增情境）時，組合裡要拿掉的條件組與條件同樣先晃一下再收起。
  function announceClearedComposition() {
    if (!global.JetMotion || typeof global.JetMotion.removing !== 'function') { return; }
    global.JetMotion.removing({ within: ['filter-panel'], only: '.scenario-set, .rule-row' });
  }

  // 晚一點才讀回來的內容（例如打開「查看結果」後才載入的傳票），整塊從原本的高度平順變成新的高度，
  // 不在讀回來的那一刻一下子撐開（使用者 2026-10-13 要求展開與收合一致）。element 要有 data-motion-key，app.js 的 JetMotion 才認得是同一塊。
  function fillSmoothly(element, fill) {
    if (element && global.JetMotion && typeof global.JetMotion.morph === 'function' && element.hasAttribute('data-motion-key')) {
      global.JetMotion.morph(element, fill);
      return;
    }
    fill();
  }

  // 動態被系統設定關掉時（減少動態、高對比佈景），捲動也直接到位。
  function smoothScroll() {
    return !!global.JetMotion && typeof global.JetMotion.allowed === 'function' && global.JetMotion.allowed() ? 'smooth' : 'auto';
  }

  function populationScopeHtml(state) {
    var committed = committedPopulationScope(state);
    var savedCount = state.filter.savedScenarios.length;
    var needsResave = savedCount > 0 && !committed;
    var statusClass = needsResave
      ? 'population-scope__status population-scope__status--pending'
      : 'population-scope__status';
    // 「尚無可用版本」改說已儲存的情境尚未套用目前規則（使用者 2026-10-14 第三輪勾選頁確認 R3-24）。
    var statusText = committed ? '在 GL 中篩選分錄。已儲存版本：查核期間內的分錄。' : '已儲存的篩選情境尚未套用目前規則。';


    return (
      '<section class="population-scope" aria-labelledby="population-scope-heading">' +
        '<div class="population-scope__head">' +
          '<div>' +
            '<h3 class="population-scope__title" id="population-scope-heading">GL：查核期間內的分錄</h3>' +
          '</div>' +
        '</div>' +
        '<p class="population-scope__definition">依本案過帳狀態設定，總帳日期落在查核期間內的分錄列入 GL；日期空白或期間外的分錄列入 GL(未納入測試)。</p>' +
        '<div class="' + statusClass + '"' + (needsResave ? '' : ' hidden') + '>' +
          '<span>' + Ui.esc(statusText) + '</span>' +
          (needsResave
            ? Ui.filterScenarioProblemsHtml(state) +
              '<p class="population-scope__notice">上列情境全部修改並儲存或移除後，才會顯示高風險條件矩陣與全部符合條件的分錄，也才能產生條件篩選報告與工作底稿。</p>' +
              '<button type="button" class="btn btn--ghost btn--tiny" data-action="resave-scenarios">' +
                '重新檢查並儲存情境</button>'
            : '') +
        '</div>' +
      '</section>'
    );
  }

  // 第五步的三組頁籤（工作區、條件來源、矩陣檢視）都用共用的頁籤寫法（Ui.tabAttributes），選中的寫 aria-selected。
  function paneButton(key, label) {
    return '<button type="button" data-filter-pane-select="' + key + '"' +
      Ui.tabAttributes(FilterStep.viewState.workspacePane === key, 'filter-pane-' + key, 'filter-tab-' + key) + '>' + Ui.esc(label) + '</button>';
  }

  // bump 全重繪後回填「已展開」惰性面板的內容（hidden 與按鈕文字已在 HTML 生成時還原；這裡補內容：
  // 已存情境詳情與矩陣用 viewState 快取回填、無快取才重抓——見 ensureScenarioPreview／ensureMatrixContent）。
  function restoreViewState(container) {
    var draftPreview = Store.getState().filter.preview;
    if (draftPreview && draftPreview.voucherPage) {
      Ui.FilterVouchers.mount(container.querySelector('[data-bind="preview-pane-body"] .filter-voucher-host'), draftPreview);
    }
    if (FilterStep.viewState.workspacePane === 'saved') { restoreSavedViews(container); }
  }

  function restoreSavedViews(container) {
    Object.keys(FilterStep.viewState.openScenarios).forEach(function (k) {
      if (FilterStep.viewState.openScenarios[k]) { ensureScenarioPreview(container, Number(k)); }
    });
    if (FilterStep.viewState.matrixOpen) { ensureMatrixContent(container); }
  }

  // 返回可見的查看按鈕，不聚焦已收起的操作選單；原列消失後改回同一位置的下一列、
  // 末列則回前一列，清單歸零時回清單標題，不讓焦點落回 document body。
  function restoreScenarioRemovalFocus(container, index) {
    function target() {
      var current = document.querySelector('.filter-workspace') || container;
      var buttons = Array.prototype.slice.call(current.querySelectorAll(
        '[data-action="toggle-scenario"]'));
      if (buttons.length > 0) { return buttons[Math.min(index, buttons.length - 1)]; }
      return current.querySelector('[data-bind="saved-scenarios-title"]');
    }

    if (global.JetFocus) {
      global.JetFocus.defer(target);
    } else {
      global.setTimeout(function () {
        var element = target();
        if (element) { element.focus(); }
      }, 0);
    }
  }

  // 頁籤與摺疊區的檢視狀態（原本在 bind 開頭那一段）。
  function bindWorkspacePanes(container) {
    container.querySelectorAll('[data-filter-pane-select]').forEach(function (button) {
      button.addEventListener('click', function () {
        FilterStep.viewState.workspacePane = button.dataset.filterPaneSelect;
        Ui.selectTab(button);
        showOnly(container.querySelectorAll('[data-filter-pane]'), function (pane) { return pane.dataset.filterPane === FilterStep.viewState.workspacePane; },
          function () { if (FilterStep.viewState.workspacePane === 'saved') { restoreSavedViews(container); } });
      });
    });
    container.querySelectorAll('[data-condition-source]').forEach(function (button) {
      button.addEventListener('click', function () {
        FilterStep.viewState.conditionSource = button.dataset.conditionSource;
        showOnly(container.querySelectorAll('[data-condition-pane]'), function (pane) { return pane.dataset.conditionPane === FilterStep.viewState.conditionSource; });
        Ui.selectTab(button);
      });
    });
    container.querySelectorAll('[data-filter-disclosure]').forEach(function (details) {
      details.addEventListener('toggle', function () { FilterStep.viewState.disclosures[details.dataset.filterDisclosure] = details.open; });
    });
  }

  // 掛到共用的 FilterStep，供其他檔案與測試取用。
  FilterStep.categoryPickerViews = categoryPickerViews;
  FilterStep.categoryPickerView = categoryPickerView;
  FilterStep.motionKeys = motionKeys;
  FilterStep.EMPTY_FIRST_GROUP = EMPTY_FIRST_GROUP;
  FilterStep.motionKey = motionKey;
  FilterStep.groupMotionKey = groupMotionKey;
  FilterStep.keepMotionKey = keepMotionKey;
  FilterStep.showOnly = showOnly;
  FilterStep.revealAddedRule = revealAddedRule;
  FilterStep.POPULATION_SCOPE_AUDIT = POPULATION_SCOPE_AUDIT;
  FilterStep.selectedPopulationScope = selectedPopulationScope;
  FilterStep.committedPopulationScope = committedPopulationScope;
  FilterStep.populationScopeLabel = populationScopeLabel;
  FilterStep.setFieldError = setFieldError;
  FilterStep.clearFieldError = clearFieldError;
  FilterStep.freshViewState = freshViewState;
  FilterStep.draftResponseGuard = draftResponseGuard;
  FilterStep.matrixResponseGuard = matrixResponseGuard;
  FilterStep.scenarioResponseGuard = scenarioResponseGuard;
  FilterStep.invalidateScenarioResponseGuards = invalidateScenarioResponseGuards;
  FilterStep.dataGeneration = dataGeneration;
  FilterStep.resultKey = resultKey;
  FilterStep.sameGeneration = sameGeneration;
  FilterStep.syncViewState = syncViewState;
  FilterStep.requirementNote = requirementNote;
  FilterStep.ownRequirementNote = ownRequirementNote;
  FilterStep.announceRemoval = announceRemoval;
  FilterStep.announceClearedComposition = announceClearedComposition;
  FilterStep.fillSmoothly = fillSmoothly;
  FilterStep.smoothScroll = smoothScroll;
  FilterStep.populationScopeHtml = populationScopeHtml;
  FilterStep.paneButton = paneButton;
  FilterStep.restoreViewState = restoreViewState;
  FilterStep.restoreSavedViews = restoreSavedViews;
  FilterStep.restoreScenarioRemovalFocus = restoreScenarioRemovalFocus;
  FilterStep.bindWorkspacePanes = bindWorkspacePanes;
})(window);
