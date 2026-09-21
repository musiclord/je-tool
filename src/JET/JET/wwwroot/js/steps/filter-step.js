/*
  第五步：進階條件篩選。
  前端只組裝 AST 與渲染；條件由後端轉參數化 SQL 評估，前端不計算規則。

  條件入口與本次篩選並排；命名在保存時出現。已保存情境和矩陣另有檢視入口。
  純檢視狀態留在本模組，切換不改 AST；KCT、自訂條件與報告的業務判定由後端負責。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;
  var selectedRule = null;

  function revealAddedRule(rule) {
    selectedRule = rule;
    global.JetFocus.defer(function () {
      var draft = Store.getState().filter.draft;
      var gi = draft.groups.findIndex(function (group) { return group.rules.indexOf(rule) >= 0; });
      if (gi < 0) { return null; }
      var ri = draft.groups[gi].rules.indexOf(rule);
      var row = document.querySelector('.filter-workspace .rule-row[data-gi="' + gi + '"][data-ri="' + ri + '"]');
      if (!row || !row.getClientRects().length) { return null; }
      row.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' });
      return Array.from(row.querySelectorAll('.rule-row__controls input:not([disabled]), .rule-row__controls select:not([disabled]), .rule-row__controls textarea:not([disabled]), .rule-row__controls button:not([disabled])'))
        .find(function (control) { return control.getClientRects().length > 0; });
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
     依 NN/g「錯誤訊息就近於欄位、用多重線索（紅框＋紅字）、別只靠顏色」。
     這是純呈現輔助：標紅框、在欄位（form__row）下方插一行紅字；權威驗證仍在後端。
     原掛在 ui-core（JetUi），因全前端只有本步驟使用而搬入本檔。 */

  // 在欄位上加紅框並在其所在 form__row 下方顯示一行訊息。message 省略時沿用既有文字。
  function setFieldError(input, message) {
    if (!input) { return; }
    input.classList.add('form__input--error');
    input.setAttribute('aria-invalid', 'true');
    var row = input.parentNode;
    if (!row) { return; }
    var err = row.querySelector('.field-error');
    if (!err) {
      err = document.createElement('span');
      err.className = 'field-error';
      err.setAttribute('role', 'alert');
      row.appendChild(err);
    }
    if (message != null) { err.textContent = message; }
  }

  // 清掉某欄位的錯誤狀態（紅框與訊息）。使用者修正欄位時即呼叫，避免「持續責備」。
  function clearFieldError(input) {
    if (!input) { return; }
    input.classList.remove('form__input--error');
    input.removeAttribute('aria-invalid');
    var row = input.parentNode;
    var err = row ? row.querySelector('.field-error') : null;
    if (err && err.parentNode) { err.parentNode.removeChild(err); }
  }

  /* ============================================================================
     檢視狀態（module 層；絕不進 draft、絕不進 wire）。
     草稿的每次結構變更都走 setFilterDraft → bump 全重繪；展開狀態若只活在 DOM，使用者每加一條
     條件，挑選區就收回、已存情境詳情就收合並丟掉已載入的預覽——操作連續性全毀。因此把這些
     純檢視狀態放到重繪之外。放 module 層而非 Store：比照匯入精靈「模組區域狀態」的既有慣例
     （見 state.js touch() 的註解），Store 不需要知道單一步驟的展開細節；生命週期由本模組在每次
     render 時自行對齊（syncViewState）。
       customPickerOpen —— 自訂篩選條件挑選區展開狀態。
       openScenarios    —— 已存情境詳情展開狀態（index → true）。
       scenarioPreviews —— 已存情境惰性命中預覽快取（index → filter.preview 回傳的 scenario 物件）。
                           草稿編輯不影響已存情境的命中（已存定義只在 commit 時變），跨草稿編輯保留
                           快取是安全的。
       matrixOpen / matrixData —— 高風險條件矩陣展開狀態與情境摘要（tagMatrixScenarios）快取。
     重置時機（三個訊號、範圍遞減）：
       1. 專案切換（projectId 變）→ 全重置。
       2. 已存清單被替換（savedScenarios 參照變＝commit 成功、移除情境、resume、resetWorkflow）
          → 重置詳情與矩陣——index 對位與命中都可能已變。
       3. 已保存 resultRef 換版（revision／scope 變）→ 只作廢資料快取，保留展開狀態。
       4. Filter 依賴的底層資料世代變（GL／科目配對／授權清單／行事曆）→ 只作廢資料快取：
          已存定義沒變、展開狀態保留，但命中是舊資料算的，restore 會就地重抓。TB 不在此集合。
     ============================================================================ */
  function freshViewState() {
    return {
      customPickerOpen: true,
      workspacePane: 'filter',
      conditionSource: 'kct',
      legacyLetter: 'A',
      legacyField: '',
      customSubject: '',
      saveOpen: false,
      disclosures: {},
      matrixView: 'scenarios',
      openScenarios: {},
      scenarioPreviews: {},
      matrixOpen: false,
      matrixData: null,
      pendingRemovalIndex: null
    };
  }
  var viewState = freshViewState();
  var viewProjectKey = null;
  var viewSavedRef = null;
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

  // 底層資料世代只追 filter 真正依賴的 GL／科目配對／授權名單／行事曆。TB 不參與 filter，
  // 因此 TB 匯入或重投影不得無故作廢命中快取。草稿編輯也完全不碰這些參照。
  function dataGeneration(state) {
    var imp = state.importState;
    return [imp.gl, imp.accountMapping, imp.authorizedPreparer, imp.calendar,
      state.mapping.gl.committed];
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
    if (key !== viewProjectKey) {
      draftResponseGuard.invalidate();
      invalidateScenarioResponseGuards();
      matrixResponseGuard.invalidate();
      viewProjectKey = key;
      viewSavedRef = state.filter.savedScenarios;
      viewDataGen = gen;
      viewResultKey = resultKey(state);
      viewState = freshViewState();
      return;
    }
    if (state.filter.savedScenarios !== viewSavedRef) {
      invalidateScenarioResponseGuards();
      matrixResponseGuard.invalidate();
      viewSavedRef = state.filter.savedScenarios;
      viewState.openScenarios = {};
      viewState.scenarioPreviews = {};
      viewState.matrixOpen = false;
      viewState.matrixData = null;
      viewState.pendingRemovalIndex = null;
    }
    var nextResultKey = resultKey(state);
    if (nextResultKey !== viewResultKey) {
      invalidateScenarioResponseGuards();
      matrixResponseGuard.invalidate();
      viewResultKey = nextResultKey;
      viewState.scenarioPreviews = {};
      viewState.matrixData = null;
    }
    if (!sameGeneration(gen, viewDataGen)) {
      invalidateScenarioResponseGuards();
      matrixResponseGuard.invalidate();
      viewDataGen = gen;
      viewState.scenarioPreviews = {};
      viewState.matrixData = null;
    }
  }

  // 鏡像閘門：依科目配對 target 內容資格與授權清單匯入狀態提供型別（權威驗證在後端）。
  function availableRuleTypes() {
    var state = Store.getState();
    var imp = state.importState;
    var hasRdeFields = Ui.committedRdeFields(state).length > 0;
    return Ui.FILTER_RULE_TYPES.filter(function (t) {
      var requirement = t.accountMappingRequirement;
      var mapping = imp.accountMapping;
      var mappingEligible = !requirement || (!!mapping && (
        (requirement === 'any' && mapping.hasAnyCategory) ||
        (requirement === 'revenue' && mapping.hasRevenue) ||
        (requirement === 'revenueAndCounterpart' && mapping.hasRevenue && mapping.hasCounterpart)
      ));
      return mappingEligible &&
        (!t.requiresAuthorizedPreparers || !!imp.authorizedPreparer) &&
        (!t.requiresRdeFields || hasRdeFields);
    });
  }

  // 目前案件已提交的攸關資料元素欄位；沒有時 typed 條件卡停用並說明原因。
  function rdeFieldOptions() {
    return Ui.committedRdeFields(Store.getState());
  }

  function rdeFieldValueType(fieldId) {
    var hit = rdeFieldOptions().filter(function (f) { return f.fieldId === fieldId; })[0];
    return hit ? hit.valueType : null;
  }

  function accountMappingRequirementNote(ruleType) {
    var type = Ui.FILTER_RULE_TYPES.find(function (item) { return item.value === ruleType; });
    if (type && type.requiresRdeFields && rdeFieldOptions().length === 0) {
      return '需先在欄位配對勾選額外欄位';
    }
    var requirement = type && type.accountMappingRequirement;
    if (!requirement) { return ''; }

    var mapping = Store.getState().importState.accountMapping;
    if (!mapping) { return '需先匯入科目配對'; }
    if (requirement === 'any') {
      return mapping.hasAnyCategory ? '' : '科目配對需至少一筆非空白分類';
    }
    if (requirement === 'revenue') {
      return mapping.hasRevenue ? '' : '科目配對需包含 Revenue 分類';
    }
    if (requirement === 'revenueAndCounterpart') {
      if (!mapping.hasRevenue) { return '科目配對需包含 Revenue 分類'; }
      return mapping.hasCounterpart ? '' : '科目配對需至少一個一般對方分類';
    }
    return '';
  }

  function availablePrescreenKeys() {
    var imp = Store.getState().importState;
    return Ui.PRESCREEN_KEY_OPTIONS.filter(function (o) {
      return (!o.requiresAccountMapping || !!imp.accountMapping) &&
        (!o.requiresAuthorizedPreparers || !!imp.authorizedPreparer);
    });
  }

  /* ============================================================================
     KCT 卡 ↔ 草稿成員：介面小、實作深的少數函式（Ousterhout）。
     一張 KCT 卡 = 一份「rule 規格」陣列（kctRuleSpecs）。加入 = 把這些規格化成真 rule、
     在每條 rule 上打一個穩定的身分標記（__kctLetter = 卡片字母）併進草稿；已選偵測 =
     草稿中是否存在帶該卡字母標記的 rule；移除 = 把所有帶該卡字母標記的 rule 挑掉。

     為何用標記而非結構簽章（取捨顯式）：先前以 type(+field/mode/prescreenKey) 的結構簽章
     猜「這條 rule 是不是這張卡帶進來的」。但 KCT 卡的簽章會與查核員「自訂」的同型別條件
     重疊——例如卡 F(customKeywords) 會被任何手動加入的自訂關鍵字條件誤判為「已選」，且移除
     時只刪「第一條」同簽章 rule，可能誤刪使用者手動加入的條件。改用精確身分標記後，這一整類
     誤判／誤刪的 edge case 直接消失（Linus）：自訂的同型別條件不帶標記，永遠不會被當成 KCT。

     __kctLetter 是 UI-only 標記（鍵名以雙底線開頭、明示「內部用途、不屬 wire 契約」），絕不可
     送進後端。所有送出點一律經 toWireScenario() 深拷貝並遞迴剝除；有 KCT marker 或已存
     source:'kct' 時另送根層 source:'kct'，groups 內每條 rule 不含任何 UI-only 鍵。
     ============================================================================ */

  // rule 身分標記鍵：標示「這條 rule 是哪張 KCT 卡帶進來的」。UI-only，絕不送 wire（由
  // toWireScenario 剝除）。集中為常數，讓打標記／偵測／剝除三處引用同一事實。
  var KCT_LETTER_KEY = '__kctLetter';
  // FilterConditionRenderer 的同傳票結構片語；read-back textContent 必須逐字鏡像後端。
  var OUTPUT_ANCHOR_LABEL = '主要條件（決定命中分錄）';
  var SAME_VOUCHER_EXPLANATION = '後續條件可由同一傳票的其他分錄列符合';

  // 草稿目前是否仍含 KCT 身分 marker。名稱／動機選填只信任此即時狀態；最後一個 KCT 被移除或改型後，
  // 不會留下 stale root source 而錯誤豁免一般情境必填。
  function hasKctMarker(scenario) {
    return (scenario.groups || []).some(function (group) {
      return (group.rules || []).some(function (rule) { return !!rule[KCT_LETTER_KEY]; });
    });
  }

  // Wire source 的單一推導點：草稿讀 marker；project.load 的 saved summary 已沒有 marker，故保留 canonical source。
  function scenarioSource(scenario) {
    return hasKctMarker(scenario) || scenario.__legacyKctSource || (scenario.source === 'kct' && !scenario.__restoredOrigins) ? 'kct' : null;
  }

  function requiresScenarioMetadata(draft) {
    return !(hasKctMarker(draft) || draft.__legacyKctSource);
  }

  // 一張 KCT 卡會建立的「rule 規格」陣列（純資料；type 必填，其餘為覆寫鍵）。
  //   kind:'type'   → 單一規格 { type: ref }，落地為 newFilterRule(ref)。
  //   kind:'preset' → 沿用 FILTER_KCT_PRESETS：newGroup 為多條規格，否則單一 overrides 規格。
  function kctRuleSpecs(item) {
    if (item.kind === 'preset') {
      var preset = Ui.FILTER_KCT_PRESETS.filter(function (p) { return p.key === item.ref; })[0];
      if (!preset) { return []; }
      return preset.newGroup ? preset.newGroup.slice() : [preset.overrides];
    }
    return item.ref ? [{ type: item.ref }] : [];
  }

  // 是否為「自成一組」的預設 KCT（目前只有 I＝非營業日：weekend OR holiday）。這類本質是情境層級的獨立
  // OR 群組，卡片 toggle 維持情境層級；其餘單規則 KCT 為組層級（見 isKctSelectedActive）。
  function isPresetNewGroup(item) {
    if (item.kind !== 'preset') { return false; }
    var preset = Ui.FILTER_KCT_PRESETS.filter(function (p) { return p.key === item.ref; })[0];
    return !!(preset && preset.newGroup);
  }

  // 把一條規格落地成真 rule，並打上該卡的身分標記：以 newFilterRule(type) 為底，套上規格的
  // 覆寫鍵（field/mode/prescreenKey/join…），最後標 __kctLetter = letter（UI-only，剝除後才送 wire）。
  function materializeRule(spec, letter) {
    var rule = Ui.newFilterRule(spec.type);
    Object.keys(spec).forEach(function (k) { rule[k] = spec[k]; });
    rule[KCT_LETTER_KEY] = letter;
    return rule;
  }

  // 已選偵測：草稿中是否存在帶該卡字母標記的 rule。純比對身分標記，不再做結構簽章猜測，
  // 故使用者自訂的同型別條件（不帶標記）永遠不會誤觸已選。空規格＝停用卡，不算已選。
  function isKctSelected(draft, item) {
    if (kctRuleSpecs(item).length === 0) { return false; }
    return draft.groups.some(function (g) {
      return g.rules.some(function (r) { return r[KCT_LETTER_KEY] === item.letter; });
    });
  }

  // 卡片「已選」判定（組層級）：單規則 KCT 看「作用中組」是否含該字母；預設(I) 是情境層級獨立群組，看全情境。
  // 讓同一訊號可在不同組各自存在，且在作用中組 toggle 只動該組（解 r9 後 KCT 情境層 toggle 的誤刪衝突）。
  function isKctSelectedActive(draft, item) {
    if (kctRuleSpecs(item).length === 0) { return false; }
    if (isPresetNewGroup(item)) { return isKctSelected(draft, item); }
    var active = activeEditableGroup(draft);
    return !!active && active.rules.some(function (r) { return r[KCT_LETTER_KEY] === item.letter; });
  }

  // 單規則 KCT 是否「用於其他（非作用中）可編輯組」——給卡片加淡標記「也在其他組」，維持全局意識。
  function isKctUsedElsewhere(draft, item) {
    if (isPresetNewGroup(item)) { return false; }
    var active = activeEditableGroup(draft);
    return draft.groups.some(function (g) {
      return !g.__kctPresetGroup && g !== active &&
        g.rules.some(function (r) { return r[KCT_LETTER_KEY] === item.letter; });
    });
  }

  // KCT 卡視覺狀態（給 kctPickerHtml）。把「情境層級的預設(I)」和「組層的單規則」分開，避免 I 用組層藍色
  // 高亮而被誤讀為「已加入作用中組」：
  //   'preset'    = 預設(I)，已加入情境（不論作用中組）——黃色「情境層級」標記。
  //   'selected'  = 單規則、在作用中組（藍色高亮）。
  //   'elsewhere' = 單規則、只在別的（非作用中）組（藍邊「也在其他組」）。
  //   'none'      = 未加入。
  function kctCardState(draft, item) {
    if (isPresetNewGroup(item)) { return isKctSelected(draft, item) ? 'preset' : 'none'; }
    if (isKctSelectedActive(draft, item)) { return 'selected'; }
    if (isKctUsedElsewhere(draft, item)) { return 'elsewhere'; }
    return 'none';
  }

  // 加入：把該卡的規格落地成帶標記的 rule 併進草稿（就地修改傳入 draft）。
  //   preset 的 newGroup（如 I：非營業日 weekend OR holiday）自成一組帶入（組內 OR、連接器 AND），打 __kctPresetGroup 標記；
  //   其餘單規則併入最後一個「非預設」群組（沒有就先開一個 AND 群組）——多卡累積成同一情境（預設 AND）。
  function addKctToDraft(draft, item) {
    var specs = kctRuleSpecs(item);
    if (specs.length === 0) { return; }

    var preset = item.kind === 'preset'
      ? Ui.FILTER_KCT_PRESETS.filter(function (p) { return p.key === item.ref; })[0]
      : null;

    // 預設 I（非營業日 = weekend OR holiday）自成一組：組內固定「任一(OR)」（本質語意：命中任一非營業日即算）；
    // 與前一組的連接器預設「全部(AND)」（抉擇 2：多選 KCT 預設 AND）。群組打 __kctPresetGroup 標記，
    // 讓單規則 KCT 的落點略過它（避免把單條件併入 I 的 OR 群組而違反 AND 預設）。
    // __kctPresetGroup 是 UI-only：toWireScenario 重建 group 為 {join, rules}，此鍵自然不外洩。
    if (preset && preset.newGroup) {
      draft.groups.push({
        join: 'AND',
        matchScope: 'row',
        __kctPresetGroup: true,
        rules: specs.map(function (s) {
          var r = materializeRule(s, item.letter);
          r.join = 'OR';
          return r;
        })
      });
      return;
    }

    // 單規則 KCT（A/C/D/E/F/G/H/J）：累積進「作用中」群組（activeEditableGroup）；沒有可編輯組就新建
    // 一組、組合器預設「全部(AND)」並設為作用中。新規則 join 取該群組現有組合器以維持群組內一致。
    var target = activeEditableGroup(draft);
    if (!target) {
      target = { join: 'AND', matchScope: 'row', rules: [] };
      draft.groups.push(target);
      setActiveGroup(draft, target);
    }
    var combinator = groupMatchScope(target) === 'sameVoucher'
      ? 'AND'
      : (target.rules.length ? groupCombinator(target) : 'AND');
    specs.forEach(function (spec) {
      var r = materializeRule(spec, item.letter);
      r.join = combinator;
      target.rules.push(r);
    });
  }

  // 移除：把所有帶該卡字母標記的 rule 從草稿挑掉（就地修改傳入 draft）。只刪自身標記者——使用者手動
  // 加入的同型別條件（無標記）絕不被動到。組的去留依「組身分」判斷：只丟棄本次操作清空的預設組
  //（如 I 的獨立群組，兩條 rule 帶同一字母、移除後變空即丟，與區塊移除鈕 remove-preset-group 的
  // splice 行為對齊）；使用者自建的空組或變空的可編輯組一律保留——與 removeKctFromActiveGroup
  //「不丟棄變空的作用中組」政策一致，作用中指標因此不跳動。
  function removeKctFromDraft(draft, item) {
    draft.groups = draft.groups.filter(function (g) {
      var kept = g.rules.filter(function (r) { return r[KCT_LETTER_KEY] !== item.letter; });
      var emptiedNow = g.rules.length > 0 && kept.length === 0;
      g.rules = kept;
      return !(g.__kctPresetGroup && emptiedNow);
    });
  }

  // 取消單規則 KCT：只從「作用中組」移除該字母的 rule（同訊號在別組不動）；不丟棄變空的作用中組
  //（使用者正在編輯它，空組於 toWireScenario 送出時才略過）。
  function removeKctFromActiveGroup(draft, item) {
    var active = activeEditableGroup(draft);
    if (!active) { return; }
    active.rules = active.rules.filter(function (r) { return r[KCT_LETTER_KEY] !== item.letter; });
  }

  // wire 投影（單一收斂點）：把一個情境（草稿或已存）深拷貝成 filter.preview / filter.commit 的
  // wire 形狀——{ source?:'kct', name, rationale, groups }，且 groups 內每條 rule 都遞迴剝除 __kctLetter 等
  // 任何 UI-only 鍵。深拷貝是關鍵：淺取 groups 會讓 wire 仍指向帶標記的 rule 物件。所有送往後端的
  // scenario/draft 一律經此，確保 UI-only 標記絕不外洩到 wire（Karpathy：不靜默假設、顯式剝除）。
  function toWireScenario(s) {
    var all = s.groups || [];
    // 預設(I) 群組一律排在所有可編輯組之後 ⇒ 後端 left-fold 把它 AND 到整個情境（情境層級，Option A），
    // 不受使用者「先加 I 再加組」的插入順序影響（否則 I 會只 AND 到前一組而非整體）。
    var ordered = all.filter(function (g) { return !g.__kctPresetGroup; })
      .concat(all.filter(function (g) { return g.__kctPresetGroup; }));
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
              // 剝除所有 __ 前綴鍵（UI-only 慣例，如 __kctLetter）——按前綴而非逐鍵點名，未來新增
              // UI 鍵不會漏剝而洩進 wire（後端會把原始 JSON 全文持久化，洩漏即永久留存）。
              if (k.indexOf('__') !== 0) {
                clean[k] = Array.isArray(r[k]) ? r[k].slice() : r[k];
              }
            });
            return clean.type === 'typed' ? typedWireRule(clean) : clean;
          })
        };
      })
    };
    // 2026-09-05 到 06 那一輪保存的情境可能還帶 exclusions；原樣送出讓後端以 invalid_scenario 指路改用否定模式，
    // 不在畫面默默丟掉。
    if (Array.isArray(s.exclusions) && s.exclusions.length) { wire.exclusions = s.exclusions; }
    var source = scenarioSource(s);
    var originGroups = ordered.filter(function (g) { return (g.rules || []).length > 0; });
    if (s.editorOrigins && !s.__restoredOrigins) { wire.editorOrigins = JSON.parse(JSON.stringify(s.editorOrigins)); }
    else if (hasKctMarker(s) || s.__restoredOrigins || (!s.__legacyKctSource && s.source !== 'kct')) {
      wire.editorOrigins = { version: 1, legacyKctSource: !!s.__legacyKctSource, groups: originGroups.map(function (g) {
        return { presetGroup: !!g.__kctPresetGroup, letters: g.rules.map(function (r) { return r[KCT_LETTER_KEY] || null; }) };
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
  // 四處一律讀這裡，保證「畫面顯示＝實際送出」。取值：第二個可編輯組存在時取其 join（後端 left-fold
  // 下第一組的 join 沒有左運算元、不參與語意），否則預設 'OR'——與段控首次出現（加到第二組）時的
  // 顯示一致。
  function scenarioJoin(draft) {
    var editable = (draft.groups || []).filter(function (g) { return !g.__kctPresetGroup; });
    return editable[1] ? effectiveRuleJoin(editable[1]) : 'OR';
  }

  // 草稿送出點專用投影：先把所有可編輯組的 join 收斂成有效組間運算子（單一情境層運算子模型；預設 I
  // 組不動——固定 AND、恆排最後），再走 toWireScenario。這保證任何未預見的操作路徑下「顯示＝送出」
  // 都成立；收斂直接寫回草稿，讓狀態與段控恆一致。為何不放進 toWireScenario：已存情境重投影
  //（savedScenarios.map(toWireScenario)、惰性預覽、移除情境）時 __kctPresetGroup 已被剝除，無從分辨
  // I 組，在那裡 normalize 會把 I 的固定 AND 誤改成組間運算子（OR 情境下語意直接錯掉）。已存情境在
  // 保存當下已經過本收斂，重投影維持原樣即正確。
  function toWireDraft(draft) {
    if (draft.__preserveJoins) { return toWireScenario(draft); }
    var join = scenarioJoin(draft);
    (draft.groups || []).forEach(function (g) {
      if (!g.__kctPresetGroup) { g.join = join; }
    });
    return toWireScenario(draft);
  }

  // 作用中（active）群組：UI-only 標記 __active，標示「上方面板新增的條件要進哪一組」。恆有至多一個非預設
  // 群組帶 __active；toWireScenario 把 group 重建為 {join, rules}，此鍵自然不外洩（同 __kctPresetGroup）。
  // 取作用中組：帶 __active 的非預設組 → 否則回退最後一個非預設組 → 再無則 null。
  function activeEditableGroup(draft) {
    var editable = (draft.groups || []).filter(function (g) { return !g.__kctPresetGroup; });
    var active = null;
    editable.forEach(function (g) { if (g.__active) { active = g; } });
    if (active) { return active; }
    return editable.length ? editable[editable.length - 1] : null;
  }

  // 設作用中：先清掉所有 __active，再標記目標（非預設組）。傳 null 僅清空。
  function setActiveGroup(draft, group) {
    (draft.groups || []).forEach(function (g) { if (g.__active) { delete g.__active; } });
    if (group && !group.__kctPresetGroup) { group.__active = true; }
  }

  // 某組內的 KCT 字母（依 checklist A→J 排序、去重）。
  function kctLettersInGroup(group) {
    var has = {};
    group.rules.forEach(function (r) { if (r[KCT_LETTER_KEY]) { has[r[KCT_LETTER_KEY]] = true; } });
    return Ui.FILTER_KCT_CHECKLIST
      .filter(function (it) { return has[it.letter]; })
      .map(function (it) { return it.letter; });
  }

  // 某組是否含任何 KCT 規則（帶 __kctLetter）——判斷「結構變動是否涉及 KCT 字母」，決定要不要重算命名
  //（只有涉及 KCT 才重算，避免在純自訂情境誤清手改名稱）。
  function groupHasKct(group) {
    return !!group && group.rules.some(function (r) { return r[KCT_LETTER_KEY]; });
  }

  // KCT 命名（使用者指定，可手改）：單一可編輯組沿用「全部所選字母排一排」（如 G+H、G+I+J），最乾淨；多
  // 可編輯組則以「組」區分——每組字母用 '+' 串、組間用 '｜' 分隔（只有自訂條件的組顯示「自訂」、全空略過），
  // 讓名稱看得出結構；預設(I) 字母附在最後一個 token。例：第1組{G,H}、第2組{J} → G+H｜J；跨組 → G+H｜G+J。
  // KCT 命名（使用者指定，可手改）：每個可編輯組一個字母 token（依 checklist 排序、'+' 串；只有自訂條件的
  // 組顯「自訂」、全空略過）；預設(I) 是情境層級獨立區塊（Option A），命名為「自己的 token」附在最後。token
  // 之間以 '｜' 分隔（單一 token 時不加分隔）。例：G、G+H、G+H｜G+J、G｜H｜I、G｜I、G｜自訂。
  function kctScenarioName(draft) {
    var tokens = [];
    draft.groups.forEach(function (g) {
      if (g.__kctPresetGroup) { return; }
      var letters = kctLettersInGroup(g);
      if (letters.length) { tokens.push(letters.join('+')); }
      else if (g.rules.length) { tokens.push('自訂'); }
    });
    var presetLetters = [];
    draft.groups.forEach(function (g) {
      if (g.__kctPresetGroup) {
        kctLettersInGroup(g).forEach(function (L) { if (presetLetters.indexOf(L) < 0) { presetLetters.push(L); } });
      }
    });
    if (presetLetters.length) { tokens.push(presetLetters.join('+')); }
    return tokens.join('｜');
  }

  // KCT 動機（使用者指定）：每個所選 KCT 卡的詳細說明逐行列出（字母＋清單 label）。
  function kctScenarioRationale(draft) {
    return Ui.FILTER_KCT_CHECKLIST
      .filter(function (item) { return isKctSelected(draft, item); })
      .map(function (item) { return item.letter + '：' + item.label; })
      .join('\n');
  }

  // 勾選/取消 KCT 後重算名稱與動機並寫回草稿：KCT 選取主導命名；無 KCT 規則時清空兩欄，交回手動填寫
  //（純自訂條件的情境）。就地修改傳入 draft。
  // 手改保留（規格 §10「名稱與動機皆可手改」）：使用者把欄位改成非空值後（__nameDirty／__rationaleDirty，
  // draft 根層 UI-only 鍵，沿用 __ 慣例；toWireScenario 逐欄挑選 name/rationale/groups，不會外洩），
  // 自動命名不再覆寫該欄；欄位清空即重設旗標、自動命名恢復接手。
  function applyKctNaming(draft) {
    var name = kctScenarioName(draft);
    if (!draft.__nameDirty) { draft.name = name; }
    if (!draft.__rationaleDirty) { draft.rationale = name ? kctScenarioRationale(draft) : ''; }
  }

  /* ============================================================================
     區塊 1：KCT條件 選取（A–J 可複選 toggle）
     十顆卡片由 FILTER_KCT_CHECKLIST 一份資料驅動。已選/未選兩態由身分標記得出（isKctSelected：
     草稿中是否存在帶該卡字母標記 __kctLetter 的 rule），不再做結構簽章猜測。
     B（Phase 2 佔位）與未符合逐條科目配對內容資格的 A/C/D：停用＋原因註記，不綁 toggle。
     ============================================================================ */
  function kctPickerHtml(draft) {
    var selectedCount = 0;
    var unavailable = [];
    var cells = Ui.FILTER_KCT_CHECKLIST.map(function (item) {
      // Phase 2 佔位優先；其次依逐條 target 內容資格顯示科目配對鏡像閘門。
      var mappingNote = item.kind === 'type' && !item.disabled
        ? accountMappingRequirementNote(item.ref)
        : '';
      var blockedByMapping = !!mappingNote;
      var disabled = item.disabled || blockedByMapping;
      var note = item.disabled
        ? item.note
        : mappingNote;

      var state = disabled ? 'none' : kctCardState(draft, item);
      if (state === 'selected') { selectedCount++; } // 計數＝作用中組的組層 KCT（情境層級 I 不計入）

      var cls = 'picker-card picker-card--kct' +
        (state === 'selected' ? ' picker-card--selected' : '') +
        (state === 'elsewhere' ? ' picker-card--elsewhere' : '') +
        (state === 'preset' ? ' picker-card--preset' : '') +
        (disabled ? ' picker-card--disabled' : '');
      var pressed = (state === 'selected' || state === 'preset') ? 'true' : 'false';

      var card = '<button type="button" class="' + cls + '"' +
          ' data-kct-letter="' + item.letter + '"' +
          ' aria-pressed="' + pressed + '"' +
          (disabled ? ' disabled aria-disabled="true"' : '') + '>' +
        '<span class="picker-card__letter" aria-hidden="true">' + Ui.esc(item.letter) + '</span>' +
        '<span class="picker-card__label" title="' + Ui.esc(item.label) + '">' + Ui.esc(item.label) + '</span>' +
        (note ? '<span class="picker-card__note">' + Ui.esc(note) + '</span>' : '') +
        (state === 'elsewhere' ? '<span class="picker-card__elsewhere">也在其他組</span>' : '') +
        (state === 'preset' ? '<span class="picker-card__preset-mark">情境層級</span>' : '') +
      '</button>';
      if (disabled) { unavailable.push(card); return ''; }
      return card;
    }).join('');

    return (
      '<section class="condition-picker condition-picker--kct">' +
        '<div class="condition-picker__head">' +
          '<h3 class="condition-picker__title">KCT條件</h3>' +
          '<span class="condition-picker__count">作用中組已選 ' + selectedCount + ' 項</span>' +
        '</div>' +
        '<p class="condition-picker__intro">點選加入或取消。非營業日 I 適用整個情境。</p>' +
        '<div class="condition-picker__grid condition-picker__grid--kct">' + cells + '</div>' +
        (unavailable.length ? '<details class="filter-unavailable"' + disclosureAttributes('unavailable-kct') +
          '><summary>尚不可用（' + unavailable.length + '）</summary><div class="condition-picker__grid">' + unavailable.join('') + '</div></details>' : '') +
      '</section>'
    );
  }

  // 自訂條件入口只選擇篩選對象；常用情境範例另有獨立入口。
  function customPickerHtml() {
    return '<section class="condition-picker condition-picker--custom">' +
      '<h3 class="condition-picker__title">自訂篩選條件</h3>' +
      addRuleBarHtml() + '<div data-legacy-host>' + legacyPickerHtml() + '</div></section>';
  }

  function legacyPickerHtml() {
    var catalog = global.JetLegacyFilters.catalogue;
    var item = catalog.conditions.find(function (entry) { return entry.letter === viewState.legacyLetter; });
    var allFields = Ui.FilterValues.fields(Store.getState());
    var selectable = allFields.filter(function (field) {
      return item.letter === 'R' ? field.type === 'date' : field.type === 'text';
    });
    var showField = item.letter === 'O' || item.letter === 'R';
    var extras = allFields.filter(function (field) { return field.extra && field.type === 'text'; });
    function options(fields, selected) { return fields.map(function (field) {
      return '<option value="' + Ui.esc(field.id) + '"' + (field.id === selected ? ' selected' : '') + '>' + Ui.esc(field.label) + '</option>';
    }).join(''); }
    return '<details class="filter-examples"' + disclosureAttributes('legacy-form') + '><summary>舊表 A–U 條件與填寫範例</summary>' +
      '<p>沿用 2024 JE 篩選表的代號。加入後直接調整右側條件；這份 A–U 與 KCT A–J 分開。</p>' +
      '<label class="form__row">舊表條件<select class="form__input" data-legacy-letter>' + catalog.conditions.map(function (entry) {
        return '<option value="' + entry.letter + '"' + (entry === item ? ' selected' : '') + '>' + Ui.esc(entry.letter + '：' + entry.label) + '</option>';
      }).join('') + '</select></label><p data-legacy-help>' + Ui.esc(item.help) + '</p>' +
      (showField ? '<label class="form__row">使用欄位<select class="form__input" data-legacy-field>' + options(selectable, viewState.legacyField || item.rules[0].field) + '</select></label>' : '') +
      (item.letter === 'P' ? '<label class="form__row">指定對象<select class="form__input" data-legacy-account><option value="codes">科目代號</option><option value="categories">科目分類</option></select></label>' : '') +
      (item.letter === 'U' ? '<label class="form__row">人員條件<select class="form__input" data-legacy-person><option value="frequency">建立人員的傳票張數</option><option value="createBy">指定建立人員</option><option value="approveBy">指定核准人員</option></select></label>' : '') +
      '<button type="button" class="btn btn--ghost" data-action="add-legacy-rule">加入 ' + item.letter + ' 條件</button>' +
      '<p>以下是原工作簿的五個填寫範例。套用會替換本次草稿，範例值和理由都可修改；原有已保存情境保留。</p>' +
      '<label class="form__row">範例 2 的部門欄位<select class="form__input" data-legacy-example-field><option value="">請選已配對的額外文字欄位</option>' + options(extras, '') + '</select></label>' +
      '<div class="condition-picker__grid">' + catalog.examples.map(function (example) {
        return '<button type="button" class="picker-card" data-legacy-example="' + example.key + '"><span class="picker-card__label">' + Ui.esc(example.label) + '</span><span class="picker-card__note">' + Ui.esc(example.combination) + '</span></button>';
      }).join('') + '</div><p data-legacy-notice role="status"></p></details>';
  }

  // 條件列的三個家族：畫面只用家族分類，wire 型別維持原樣（後端不動）。
  var RULE_FAMILIES = {
    field: { label: '欄位', types: ['fieldValue', 'drCrOnly', 'manualAuto'] },
    account: { label: '科目', types: ['accountSide', 'specialAccountCategoryPair', 'accountPair'] },
    compound: { label: '條件組合', types: ['group', 'voucher'] },
    pattern: { label: '樣態', types: ['prescreen', 'customKeywords', 'customTrailingZeros', 'customPreparerEntryCount', 'customAccountEntryCount', 'entityFrequency',
      'trailingDigits', 'revenueDebitNearQuarterEnd', 'revenueWithoutNormalCounterpart', 'manualRevenueEntry', 'preparerEqualsApprover'] },
    legacy: { label: '舊式', types: ['text', 'textSet', 'dateRange', 'numRange', 'typed'] }
  };
  var PSEUDO_FIELDS = [{ id: '__drCr', label: '借貸別' }, { id: '__isManual', label: '人工／自動' }];
  var PATTERN_PARAM_TYPES = [
    { value: 'customTrailingZeros', label: '金額尾數連續 0 的位數' },
    { value: 'customPreparerEntryCount', label: '所選母體內編製人員分錄筆數 ≤' },
    { value: 'customAccountEntryCount', label: '所選母體內科目分錄筆數 ≤' },
    { value: 'entityFrequency', label: '科目與人員統計' },
    { value: 'customKeywords', label: '摘要關鍵字（可自行編輯）' }
  ];

  function ruleFamily(rule) {
    return Object.keys(RULE_FAMILIES).filter(function (key) { return RULE_FAMILIES[key].types.indexOf(rule.type) >= 0; })[0] || 'legacy';
  }

  // 新條件共用一個對象選單，加入 activeEditableGroup；不改既有規則型別。
  function addRuleBarHtml() {
    function option(value, label, note) {
      return '<option value="' + Ui.esc(value) + '"' + (value === viewState.customSubject ? ' selected' : '') +
        (note ? ' disabled' : '') + '>' + Ui.esc(label + (note ? '（' + note + '）' : '')) + '</option>';
    }
    var choices = Ui.FilterValues.groupedFields(Store.getState()).map(function (group) {
      return '<optgroup label="' + group.label + '">' + group.fields.map(function (field) {
        return option('field:' + field.id, field.label, '');
      }).join('') + '</optgroup>';
    }).join('') + '<optgroup label="分錄性質">' + option('type:drCrOnly', '借貸別', '') + option('type:manualAuto', '人工或自動分錄', '') + '</optgroup>';
    var accountNote = accountMappingRequirementNote('accountSide');
    choices += '<optgroup label="條件組合">' + option('type:group', '同一分錄的條件括號', '') + option('type:voucher', '整張傳票或指定側的量詞', '') + '</optgroup>';
    choices += '<optgroup label="科目分類">' + option('type:accountSide', '借方或貸方分類', accountNote) +
      option('type:specialAccountCategoryPair', '借貸分類組合', accountNote) + '</optgroup>';
    choices += '<optgroup label="分錄特徵">' + option('type:prescreen', '預篩選訊號', '') +
      PATTERN_PARAM_TYPES.map(function (item) { return option('type:' + item.value, item.label, ''); }).join('') + '</optgroup>';
    return '<div class="filter-custom-add"><label for="filter-custom-subject">新增篩選條件</label>' +
      '<select id="filter-custom-subject" class="form__input" data-custom-subject><option value="">選擇欄位或項目</option>' + choices + '</select>' +
      '<button type="button" class="btn btn--ghost" data-action="add-rule"' + (viewState.customSubject ? '' : ' disabled') + '>加入條件</button>' +
      (accountNote ? '<p class="scenario-add__note">' + Ui.esc(accountNote) + '</p>' : '') + '</div>';
  }

  function customSubjectRule() {
    var value = viewState.customSubject;
    if (value.indexOf('type:') === 0) { return Ui.newFilterRule(value.slice(5)); }
    var field = Ui.FilterValues.fields(Store.getState()).find(function (item) { return item.id === value.slice(6); });
    if (!field) { return null; }
    var rule = Ui.FilterValues.create(field.type);
    if (['createBy', 'approveBy', 'accNum'].indexOf(field.id) >= 0) { rule.operator = 'in'; }
    delete rule.field; delete rule.fieldId;
    rule[field.extra ? 'fieldId' : 'field'] = field.id;
    if (field.extra && field.type === 'money') { rule.amountBasis = 'signed'; }
    return rule;
  }

  function disclosureAttributes(key) {
    return ' data-filter-disclosure="' + key + '"' + (viewState.disclosures[key] ? ' open' : '');
  }

  /* ============================================================================
     區塊 3：建立篩選情境（彙總調整）— 統一條件建構器（單一介面、把分組融入，無模式切換）。
     情境＝一個或多個「條件組(set)」，每組就是一塊淡底 well：頂部 AND/OR 組合器段控（該組 ≥2 條件才顯示）
     ＋條件清單。一組時就是乾淨一塊（看不到「組」字）；「＋ 另一組條件」是常駐普通按鈕（非模式）。≥2 組時
     組間出現一條水平軌道、藍色段控「組間 AND/OR」（父／情境層，與組內中性段控做出層級對比）。一組一種
     組合器，要混就再開組；封頂兩層。非營業日(I) 等預設群組為情境層級獨立區塊，接在所有可編輯組之後、AND 到整個情境。
     新手層：教學空狀態＋行內布林 read-back＋4 拍提示；KCT 名稱/動機自動帶入且可留白，純自訂情境必填。
     兩層 AST 不變；空群組於 toWireScenario 送出時略過（建到一半不報錯）。
     ============================================================================ */

  // 原子預設標籤：把一個預設群組（如 I）呈現為單一白話條件文字。取群組內任一 rule 的 __kctLetter →
  // 該 KCT 卡，再查 FILTER_KCT_ATOM_LABELS（以卡的 ref＝preset key 為鍵），無對映則退回卡 label。
  function presetAtomLabel(group) {
    var letter = null;
    group.rules.some(function (r) {
      if (r[KCT_LETTER_KEY]) { letter = r[KCT_LETTER_KEY]; return true; }
      return false;
    });
    var item = Ui.FILTER_KCT_CHECKLIST.filter(function (k) { return k.letter === letter; })[0];
    if (item && Ui.FILTER_KCT_ATOM_LABELS[item.ref]) { return Ui.FILTER_KCT_ATOM_LABELS[item.ref]; }
    return item ? item.label : '預設條件';
  }

  // 預設(I) 情境層級區塊：唯讀白話＋「情境層級」標籤＋移除；明示它套用到整個情境（也須符合上方條件），不屬於
  // 任何「第 N 組」（非營業日＝週末 OR 假日，結構上是巢狀 OR，2-level 模型只能自成一組）。移除＝splice 整個
  // 預設群組、取消對應 KCT 卡（見 bind 的 remove-preset-group）。
  function presetBlockHtml(group, gi) {
    return (
      '<div class="scenario-preset">' +
        '<div class="scenario-preset__head">' +
          '<span class="scenario-preset__tag">情境層級</span>' +
          '<span class="scenario-preset__label">' + Ui.esc(presetAtomLabel(group)) + '</span>' +
          '<button type="button" class="btn btn--ghost scenario-preset__remove" data-action="remove-preset-group" data-gi="' + gi + '">移除</button>' +
        '</div>' +
        '<p class="scenario-preset__note">套用到整個情境，也須符合上方條件。</p>' +
      '</div>'
    );
  }

  // 群組的比對範圍是 AST 資料，不是前端運算模式。fieldset/legend 讓下拉可報讀；
  // 比對範圍縮成組標頭上的一個小下拉，只有選「同一傳票」時才多一行說明（錨點、佐證與 AND 限制）。
  function matchScopeHtml(group, gi) {
    var current = groupMatchScope(group);
    var helpId = 'match-scope-help-' + gi;
    var options = Ui.FILTER_MATCH_SCOPE_OPTIONS.map(function (option) {
      return '<option value="' + Ui.esc(option.value) + '"' + (option.value === current ? ' selected' : '') + '>' + Ui.esc(option.label) + '</option>';
    }).join('');
    var help = current === 'sameVoucher'
      ? '第 1 條標示為「' + OUTPUT_ANCHOR_LABEL + '」；' + SAME_VOUCHER_EXPLANATION +
        '。至少需要 2 條條件，條件之間固定全部符合。'
      : (group && group.rules.some(function (rule) { return rule.type === 'accountSide'; })
          ? '借方與貸方條件要由不同列符合時，請選「同一傳票」。' : '');

    // 說明放在 fieldset 之外、組標頭的最後，獨占一行；aria-describedby 仍指向它。
    return (
      '<fieldset class="match-scope" aria-describedby="' + helpId + '">' +
        '<legend class="match-scope__legend">比對範圍</legend>' +
        '<select class="inline-select" data-group-bind="matchScope" data-gi="' + gi + '" aria-label="比對範圍">' + options + '</select>' +
      '</fieldset>' +
      (help ? '<p class="match-scope__help" id="' + helpId + '">' + help + '</p>' : '<span id="' + helpId + '" hidden></span>')
    );
  }

  // 一塊 well（一個可編輯條件組）：組合器段控（該組 ≥2 條件才顯示）＋條件清單。multi（≥2 組）時段控放進
  // 組標頭「第 N 組」旁（組內中性段控，與組間藍色段控分層）；single 時段控放 well 頂端、前綴白話 lead
  // 「條件之間」。預設(I) 不再併入 well（改為情境層級獨立區塊，見 presetBlockHtml）。
  // 一組＝一句標頭「符合以下 [全部/任一] 條件，條件在 [同一分錄/同一傳票]」＋條件列＋「＋」列。
  // 標頭在單組時也顯示（一致的閱讀順序）；多組時多「第 N 組」、作用中徽章（只影響 KCT 卡的落點）與移除。
  function setWellHtml(group, gi, setNumber, multi, isActive) {
    var sameVoucher = groupMatchScope(group) === 'sameVoucher';
    var comb = sameVoucher ? 'AND' : (group && group.rules.length ? groupCombinator(group) : 'AND');
    var combinator = group && group.rules.length > 1
      ? '<label class="filter-combinator">組內條件 <select class="inline-select" data-set-combinator data-gi="' + gi + '" aria-label="條件之間的關係"' +
        (sameVoucher ? ' disabled aria-disabled="true" title="同一傳票固定全部符合"' : '') + '>' +
        '<option value="AND"' + (comb === 'AND' ? ' selected' : '') + '>AND（全部符合）</option>' +
        '<option value="OR"' + (comb === 'OR' ? ' selected' : '') + '>OR（任一符合）</option></select></label>' : '';
    var scope = group && (group.rules.length > 1 || sameVoucher)
      ? '<details class="filter-relations"' + disclosureAttributes('relations-' + gi) + '><summary>' +
        '本組比對範圍：' + (sameVoucher ? '同一傳票' : '同一分錄') + '</summary>' + matchScopeHtml(group, gi) + '</details>' : '';
    var head = '<div class="filter-group-heading"><strong>第 ' + setNumber + ' 組</strong>' +
      (multi ? '<button type="button" class="btn btn--ghost btn--tiny" data-action="remove-set" data-gi="' + gi + '">移除這組</button>' : '') + '</div>';
    var rows = group ? group.rules.map(function (rule, ri) { return ruleRowHtml(rule, group, gi, ri); }).join('') : '';
    var settings = combinator || scope
      ? '<div class="filter-group-settings" role="group" aria-label="第 ' + setNumber + ' 組設定">' +
        combinator + scope + '</div>' : '';
    return '<div class="scenario-flat scenario-set' + (isActive ? ' scenario-set--active' : '') + '" data-group-index="' + gi + '" tabindex="-1" role="group" aria-label="第 ' + setNumber + ' 組條件">' +
      head + settings + '<div class="scenario-flat__list">' +
      (rows || '<p class="filter-group-empty">在條件總覽選擇條件，加入這一組。</p>') + '</div></div>';
  }

  // 條件組之間的連接器：做成一條水平軌道（spine），藍色段控置中跨在線上——父／情境層運算子，與組內中性
  // 段控明顯分層。data-set-join 設「所有可編輯群組」的 join 為一致值（單一組間運算子）。name 以 gi 唯一，
  // 避免 3 組以上時多個連接器共用 name 互相撞群（各連接器顯示同一致值即可）。
  function interSetConnectorHtml(joinValue, gi) {
    return (
      '<div class="set-rail">' +
        '<span class="set-rail__lead">組與組之間</span>' +
        '<select class="inline-select inline-select--scenario" data-set-join data-set-join-index="' + gi + '" aria-label="組與組之間的關係">' +
          '<option value="AND"' + (joinValue === 'AND' ? ' selected' : '') + '>AND（所有組）</option>' +
          '<option value="OR"' + (joinValue === 'OR' ? ' selected' : '') + '>OR（任一組）</option>' +
        '</select>' +
      '</div>'
    );
  }

  // 渲染所有「條件組」：每個可編輯群組一塊 well（組間插白話連接器）；預設群組（I）以情境層級獨立區塊呈現，
  // 接在所有可編輯組之後（AND 到整個情境，Option A）。
  function setsHtml(draft) {
    var editable = [];
    var presets = [];
    draft.groups.forEach(function (g, gi) {
      if (g.__kctPresetGroup) { presets.push({ group: g, gi: gi }); }
      else { editable.push({ group: g, gi: gi }); }
    });

    var presetBlocks = presets.map(function (p) { return presetBlockHtml(p.group, p.gi); }).join('');

    // 還沒有可編輯組（新草稿或只選了 I）：先畫一塊空的第 1 組，讓「＋」列一開始就在；按下去才真的建組。
    if (editable.length === 0) {
      return setWellHtml(null, draft.groups.length, 1, false, true) + presetBlocks;
    }

    var multi = editable.length >= 2;
    var active = activeEditableGroup(draft);
    var interJoin = scenarioJoin(draft); // 與 read-back、新組繼承、wire normalize 同一推導點
    var wells = editable.map(function (e, idx) {
      var isFirst = idx === 0;
      var connector = isFirst ? '' : interSetConnectorHtml(draft.__preserveJoins ? effectiveRuleJoin(e.group) : interJoin, e.gi);
      return connector + setWellHtml(e.group, e.gi, idx + 1, multi, e.group === active);
    }).join('');
    return wells + presetBlocks;
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
  function effectiveRuleJoin(rule) {
    var raw = rule && typeof rule.join === 'string' ? rule.join.trim().toUpperCase() : '';
    return raw === 'OR' ? 'OR' : 'AND';
  }

  function hasMixedEffectiveRuleJoins(group) {
    var joins = group.rules.slice(1).map(effectiveRuleJoin);
    return joins.indexOf('AND') >= 0 && joins.indexOf('OR') >= 0;
  }

  function groupReadBackExpressionHtml(group, compact) {
    var labels = group ? group.rules.map(function (r) { return ruleSummaryLabel(r, 0, compact); }) : [];
    if (groupMatchScope(group) === 'sameVoucher') {
      if (labels.length === 0) { return ''; }
      var sameVoucherExpression = Ui.esc(OUTPUT_ANCHOR_LABEL + '：' + labels[0]);
      if (labels.length > 1) {
        var sameVoucherOp = ' <span class="expr-op expr-op--group">且</span> ';
        sameVoucherExpression += sameVoucherOp +
          Ui.esc(SAME_VOUCHER_EXPLANATION + '：' + labels[1]);
        for (var evidenceIndex = 2; evidenceIndex < labels.length; evidenceIndex += 1) {
          sameVoucherExpression += sameVoucherOp + Ui.esc(labels[evidenceIndex]);
        }
      }
      return sameVoucherExpression;
    }
    if (!hasMixedEffectiveRuleJoins(group)) {
      return exprJoin(labels, groupCombinator(group) === 'OR' ? 'OR' : 'AND', 'expr-op--group');
    }

    var expression = Ui.esc(labels[0]);
    for (var i = 1; i < labels.length; i += 1) {
      var op = effectiveRuleJoin(group.rules[i]);
      var opHtml = ' <span class="expr-op expr-op--group">' + (op === 'OR' ? '或' : '且') + '</span> ';
      expression = '（' + expression + opHtml + Ui.esc(labels[i]) + '）';
    }
    return expression;
  }

  // 整句回顯（read-back）：句首白話 lead＋行內布林式，把 AND/OR 寫進去（鏡像控制項：OR＝情境層藍粗、
  // AND＝組內灰）。同時可直接作為底稿的條件邏輯。條件文字用 ruleSummaryLabel（index 0 去前綴）＋
  // presetAtomLabel。
  function readBackHtml(draft, compact) {
    var editable = [];
    var presets = [];
    draft.groups.forEach(function (g) {
      if (g.__kctPresetGroup) { presets.push(g); } else { editable.push(g); }
    });

    // 只看「有條件」的可編輯組：剛按〔＋另一組條件〕還沒填的空組不進 read-back，避免懸空運算子（如 … OR AND …）。
    var ne = editable.filter(function (g) { return g.rules.length > 0; });

    var exprHtml = '';
    if (ne.length === 1) {
      exprHtml = groupReadBackExpressionHtml(ne[0], compact);
    } else if (ne.length >= 2) {
      var sop = scenarioJoin(draft); // 不讀非空組陣列 ne[1]——空組被濾除時會與段控讀到不同組而顯示錯位
      var parts = ne.map(function (g) {
        var inner = groupReadBackExpressionHtml(g, compact);
        return g.rules.length > 1 && !hasMixedEffectiveRuleJoins(g) ? '（' + inner + '）' : inner;
      });
      if (draft.__preserveJoins && hasMixedEffectiveRuleJoins({ rules: ne })) {
        exprHtml = parts[0];
        for (var pi = 1; pi < parts.length; pi++) {
          exprHtml = '（' + exprHtml + ' <span class="expr-op expr-op--scenario">' + (effectiveRuleJoin(ne[pi]) === 'OR' ? '或' : '且') + '</span> ' + parts[pi] + '）';
        }
      } else {
        exprHtml = parts.join(' <span class="expr-op expr-op--scenario">' + (sop === 'OR' ? '或' : '且') + '</span> ');
      }
    }

    // 預設(I)：情境層級、AND 到整個情境（Option A）。附在最後；可編輯式在接 AND 預設段之前包一層
    // 括號消歧——多組本就要包；單一組含 ≥2 條時也要包：「a OR b AND 非營業日」慣例讀作
    // a OR (b AND I)，實際語意是 (a OR b) AND I。不論組內 AND/OR 一律包（AND 時括號無害）。
    if (presets.length) {
      var andOp = ' <span class="expr-op expr-op--scenario">且</span> ';
      var presetExpr = presets.map(function (p) { return Ui.esc(presetAtomLabel(p)); }).join(andOp);
      var needsParens = ne.length >= 2 || (ne.length === 1 && ne[0].rules.length >= 2);
      exprHtml = exprHtml
        ? (needsParens ? '（' + exprHtml + '）' : exprHtml) + andOp + presetExpr
        : presetExpr;
    }

    if (!exprHtml) { return ''; }

    return (
      '<p class="scenario-readback">' +
        '<span class="scenario-readback__lead">篩選條件：</span>' +
        '<span class="scenario-readback__expr">' + exprHtml + '</span>' +
      '</p>'
    );
  }

  function populationScopeHtml(state) {
    var committed = committedPopulationScope(state);
    var savedCount = state.filter.savedScenarios.length;
    var needsResave = savedCount > 0 && !committed;
    var statusClass = needsResave
      ? 'population-scope__status population-scope__status--pending'
      : 'population-scope__status';
    var committedText = committed ? '查核期間' : '尚無可用版本';
    var statusText = '測試母體固定為查核期間。已保存版本：' + committedText + '。';

    if (savedCount > 0 && !committed) {
      statusText += ' 已保存的情境來自較早的版本，請按「以查核期間重新保存」；保存完成後才會顯示矩陣、完整命中和報告。';
    }

    return (
      '<section class="population-scope" aria-labelledby="population-scope-heading">' +
        '<div class="population-scope__head">' +
          '<div>' +
            '<h3 class="population-scope__title" id="population-scope-heading">測試母體：查核期間</h3>' +
          '</div>' +
        '</div>' +
        '<p class="population-scope__definition">只納入總帳日期落在案件期間內的分錄；期外與無日期列排除。</p>' +
        '<div class="' + statusClass + '"' + (needsResave ? '' : ' hidden') + '>' +
          '<span>' + Ui.esc(statusText) + '</span>' +
          (needsResave
            ? '<button type="button" class="btn btn--ghost btn--tiny" data-action="resave-scenarios">' +
                '以查核期間重新保存</button>'
            : '') +
        '</div>' +
      '</section>'
    );
  }

  // Suggestions describe chosen conditions only; they do not invent a risk assessment.
  function suggestScenarioMetadata(draft) {
    var rules = draft.groups.reduce(function (all, group) { return all.concat(group.rules); }, []);
    if (!rules.length) { return; }
    function shortLabel(rule) {
      var text = ruleSummaryLabel(rule, 0, true);
      if (Array.from(text).length <= 28) { return text; }
      if (rule.type === 'fieldValue') {
        var label = (Ui.FilterValues.field(rule, Store.getState()) || {}).label || '欄位條件';
        return Array.from(label).length <= 28 ? label : Array.from(label).slice(0, 24).join('') + '…';
      }
      return Array.from(text).slice(0, 24).join('') + '…';
    }
    var topics = rules.map(shortLabel);
    var name = topics.slice(0, 2).join('、') + (rules.length > 2 ? '等' + rules.length + '項條件' : '');
    if (Array.from(name).length > 64) { name = topics[0] + '等' + rules.length + '項條件'; }
    var existingNames = Store.getState().filter.savedScenarios.filter(function (_, i) { return i !== draft.__editingIndex; })
      .map(function (scenario) { return String(scenario.name || '').trim().toUpperCase(); });
    var baseName = name;
    for (var suffix = 2; existingNames.indexOf(name.toUpperCase()) >= 0; suffix++) {
      name = Array.from(baseName).slice(0, 60).join('') + ' ' + suffix;
    }
    var holder = document.createElement('div');
    holder.innerHTML = readBackHtml(draft, true);
    var expression = holder.querySelector('.scenario-readback__expr');
    var rationale = expression ? '檢視符合以下條件的分錄：' + expression.textContent.trim() + '。' : '';
    if (Array.from(rationale).length > 180) {
      var description = topics.join('；');
      if (Array.from(description).length > 130) { description = topics.slice(0, 2).join('；') + '等' + rules.length + '項條件'; }
      rationale = '依已設定的條件組合篩選分錄，檢視' + description + '。';
    }
    // Manual text and existing template/KCT names remain authoritative.
    var previous = draft.__suggestedMetadata || {};
    ['name', 'rationale'].forEach(function (key) {
      if (!String(draft[key] || '').trim() || (!draft['__' + key + 'Dirty'] && draft[key] === previous[key])) {
        draft[key] = key === 'name' ? name : rationale;
      }
    });
    draft.__suggestedMetadata = { name: name, rationale: rationale };
  }

  function syncSuggestedMetadata(container) {
    if (!viewState.saveOpen) { return; }
    var draft = Store.getState().filter.draft;
    var oldName = draft.name, oldRationale = draft.rationale;
    suggestScenarioMetadata(draft);
    if (oldName !== draft.name || oldRationale !== draft.rationale) {
      Store.patchFilterDraftMeta({ name: draft.name, rationale: draft.rationale, __suggestedMetadata: draft.__suggestedMetadata });
      container.querySelector('[data-bind="scenario-name"]').value = draft.name || '';
      container.querySelector('[data-bind="scenario-rationale"]').value = draft.rationale || '';
    }
  }

  function scenarioBuilderHtml(draft) {
    function ruleCount(rule) { return 1 + (rule.rules || []).reduce(function (n, child) { return n + ruleCount(child); }, 0); }
    var totalRules = draft.groups.reduce(function (n, g) { return n + g.rules.reduce(function (sum, rule) { return sum + ruleCount(rule); }, 0); }, 0);
    var editing = typeof draft.__editingIndex === 'number';
    var metadataMark = requiresScenarioMetadata(draft) ? '<em class="form__req">*</em>' : '<span class="form__optional">（KCT 條件可選填）</span>';
    return '<section class="rule-card scenario-builder" data-builder-title tabindex="-1" aria-label="篩選條件">' +
      (editing ? '<div class="filter-builder-heading"><h3 class="rule-card__title">編輯情境：' + Ui.esc(draft.name) + '</h3>' +
        '<button type="button" class="btn btn--ghost" data-action="cancel-edit-scenario">取消編輯</button></div>' : '') +
      (draft.__legacyKctSource ? '<p class="form-notice">舊情境未保存個別 KCT 卡片來源，無法還原卡片勾選；原條件與 KCT 來源保留，仍可直接編輯。</p>' : '') +
      '<div class="filter-builder-heading"><h3 class="filter-entry-title">條件組合</h3>' +
        (totalRules ? '<div class="filter-group-tools" aria-label="新增條件組"><button type="button" class="btn btn--ghost" data-action="add-set">新增條件組</button></div>' : '') + '</div>' +
      setsHtml(draft) +
      (totalRules > 1 ? '<details class="filter-readback"' + disclosureAttributes('readback') + '><summary>檢查完整條件</summary>' + readBackHtml(draft) + '</details>' : '') +
      '<p class="form-notice" data-bind="scenario-notice" role="alert" hidden></p>' +
      '<div class="panel__actions filter-primary-actions"' + (viewState.saveOpen ? ' hidden' : '') + '>' +
        '<button type="button" class="btn btn--ghost" data-action="preview-scenario">查看符合的傳票</button>' +
        '<button type="button" class="btn" data-action="open-save" aria-expanded="' + viewState.saveOpen + '">保存情境</button>' +
      '</div>' +
      '<section class="filter-save-panel" data-save-panel' + (viewState.saveOpen ? '' : ' hidden') + ' aria-label="保存篩選情境">' +
        '<h4>保存情境</h4><p>保存所有條件組，供高風險條件矩陣與工作底稿使用。</p>' +
        '<label class="form__row"><span class="form__label">情境名稱 ' + metadataMark + '</span>' +
          '<input class="form__input" type="text" data-bind="scenario-name" placeholder="例：摘要異常且金額偏高" value="' + Ui.esc(draft.name) + '"></label>' +
        '<label class="form__row"><span class="form__label">篩選動機說明 ' + metadataMark + '</span>' +
          '<textarea class="form__input" rows="2" data-bind="scenario-rationale" placeholder="說明保留這組條件的審計理由">' + Ui.esc(draft.rationale) + '</textarea></label>' +
        '<div class="panel__actions"><button type="button" class="btn" data-action="save-scenario">' + (editing ? '更新此情境' : '保存為篩選情境') + '</button>' +
          (editing ? '<button type="button" class="btn btn--ghost" data-action="save-scenario-copy">另存副本</button>' : '') +
          '<button type="button" class="btn btn--ghost" data-action="close-save">繼續調整條件</button></div>' +
      '</section></section>';
  }

  // 常用情境範本（FILTER_SCENARIO_TEMPLATES）：一鍵把草稿換成常見的審計問題，名稱與動機預填、可改。
  // 需要科目配對的範本在未匯入時停用並說原因；套用後仍走一般的預覽與保存流程。
  function templatePickerHtml(imp) {
    var state = Store.getState();
    var cards = Ui.FILTER_SCENARIO_TEMPLATES.map(function (template) {
      var note = '';
      if (template.requires === 'accountMapping' && !(imp.accountMapping && imp.accountMapping.hasAnyCategory)) { note = '需先匯入科目配對'; }
      if (template.requires === 'periodEnd' && !(state.project && state.project.periodEnd)) { note = '案件沒有查核截止日'; }
      var cls = 'picker-card picker-card--template' + (note ? ' picker-card--disabled' : '');
      return '<button type="button" class="' + cls + '" data-template-key="' + template.key + '"' +
          (note ? ' disabled aria-disabled="true"' : ' data-action="apply-template"') + ' title="' + Ui.esc(template.rationale) + '">' +
        '<span class="picker-card__label">' + Ui.esc(template.label) + '</span>' +
        (note ? '<span class="picker-card__note">' + Ui.esc(note) + '</span>' : '') +
      '</button>';
    }).join('');
    return '<details class="filter-examples"' + disclosureAttributes('examples') + '><summary>常用範例</summary>' +
      '<p>選取範例會替換本次條件，可再調整。</p>' +
      '<div class="condition-picker__grid condition-picker__grid--custom">' + cards + '</div></details>';
  }

  // 把範本落地成草稿：規則深拷貝、名稱與動機預填並標為手改（避免 KCT 自動命名覆蓋）；
  // 帶 kct 字母的範本先用 addKctToDraft 帶入該卡，再接上範本自己的條件組；期末 N 天由查核截止日算起迄。
  function applyTemplate(key) {
    var template = Ui.FILTER_SCENARIO_TEMPLATES.filter(function (item) { return item.key === key; })[0];
    if (!template) { return; }
    var state = Store.getState();
    var draft = { name: template.label, rationale: template.rationale, groups: [], __nameDirty: true, __rationaleDirty: true };
    if (template.kct) {
      var card = Ui.FILTER_KCT_CHECKLIST.filter(function (item) { return item.letter === template.kct; })[0];
      if (card) { addKctToDraft(draft, card); }
    }
    template.groups.forEach(function (group) {
      var copy = { join: group.join || 'AND', matchScope: group.matchScope || 'row', rules: group.rules.map(function (rule) {
        var fresh = JSON.parse(JSON.stringify(rule));
        if (fresh.__periodEndDays && state.project && state.project.periodEnd) {
          var parts = state.project.periodEnd.split('-').map(Number);
          var end = new Date(Date.UTC(parts[0], parts[1] - 1, parts[2]));
          var start = new Date(Date.UTC(parts[0], parts[1] - 1, parts[2] - (fresh.__periodEndDays - 1)));
          fresh.from = start.toISOString().slice(0, 10); fresh.to = end.toISOString().slice(0, 10);
        }
        delete fresh.__periodEndDays;
        return fresh;
      }) };
      draft.groups.push(copy);
      setActiveGroup(draft, copy);
    });
    Store.setFilterDraft(draft);
    Store.addMessage('已帶入範本「' + template.label + '」，可再調整條件後預覽。', 'info');
      if (global.JetFocus) { global.JetFocus.defer(function () { return document.querySelector('.filter-workspace [data-builder-title]'); }); }
  }

  // 一條條件＝一句話：家族小標、對象、比較方式、值、移除。列上沒有型別下拉；要換家族就移除再加。
  // 條件列不再有逐條 AND/OR——群組內的結合由組標頭的「符合以下全部／任一條件」統一決定。
  function ruleRowHtml(rule, group, gi, ri, insideVoucher) {
    if (rule.type === 'group' || rule.type === 'voucher') { return compoundRuleHtml(rule, gi, ri, insideVoucher, group); }
    var family = ruleFamily(rule);
    var sameVoucher = groupMatchScope(group) === 'sameVoucher';
    var scopeLabel = sameVoucher
      ? (ri === 0
          ? '<span class="rule-row__field-label">' + Ui.esc(OUTPUT_ANCHOR_LABEL) + '</span>'
          : '<span class="rule-row__field-label">同傳票佐證（第 ' + (ri + 1) + ' 條）</span>')
      : '';
    var describedBy = sameVoucher ? ' aria-describedby="match-scope-help-' + gi + '"' : '';
    var patternHeading = family === 'pattern'
      ? (rule.type === 'prescreen' ? '預篩選訊號' :
          ((PATTERN_PARAM_TYPES.find(function (item) { return item.value === rule.type; }) ||
            Ui.FILTER_RULE_TYPES.find(function (item) { return item.value === rule.type; }) || {}).label || '分錄特徵'))
      : '';

    return (
      '<div class="rule-row rule-row--' + family + (selectedRule === rule ? ' rule-row--selected' : '') + '" data-gi="' + gi + '" data-ri="' + ri + '"' + describedBy + '>' +
        '<div class="filter-rule-heading">' +
          (selectedRule === rule ? '<span class="filter-rule-selected">選取中</span>' : '') +
          (rule.type === 'drCrOnly' ? '<strong>借貸別</strong>' : rule.type === 'manualAuto' ? '<strong>人工或自動分錄</strong>' : '') +
          (rule.type === 'accountPair' || rule.type === 'specialAccountCategoryPair' ? '<strong>借貸科目組合</strong>' : '') +
          (rule.type === 'accountSide' ? '<strong>借方或貸方分類</strong>' : '') +
          (rule.type === 'fieldValue' ? '<strong>' + Ui.esc((Ui.FilterValues.field(rule, Store.getState()) || {}).label || rule.fieldId || rule.field) + '</strong>' : '') +
          (patternHeading ? '<strong>' + Ui.esc(patternHeading) + '</strong>' : '') +
          (family === 'legacy' ? '<strong>' + Ui.esc((Ui.FILTER_RULE_TYPES.find(function (item) { return item.value === rule.type; }) || {}).label || '既有篩選條件') + '</strong>' : '') +
          '<button type="button" class="rule-row__remove" data-action="remove-rule" aria-label="移除這條條件" title="移除這條條件">移除</button></div>' +
        '<div class="rule-row__controls">' +
          scopeLabel +
          (sameVoucher ? '<button type="button" class="btn btn--ghost btn--tiny" data-action="make-primary"' + (ri === 0 ? ' disabled' : '') + '>' + (ri === 0 ? '主要條件' : '設為主要條件') + '</button>' : '') +
          ruleSubjectHtml(rule, family) + ((rule.type === 'accountPair' || rule.type === 'specialAccountCategoryPair') ? categorySelectionHtml(rule) : '') + ruleControlsHtml(rule, gi, ri) + legacyRulePolicyHtml(rule) +
        '</div>' +
      '</div>'
    );
  }

  function compoundRuleHtml(rule, gi, path, insideVoucher, outerGroup) {
    function select(key, options, current) {
      return '<select data-compound-key="' + key + '" aria-label="' + (key === 'side' ? '判斷範圍' : '符合方式') + '">' + options.map(function (o) {
        return '<option value="' + o[0] + '"' + (current === o[0] ? ' selected' : '') + '>' + o[1] + '</option>';
      }).join('') + '</select>';
    }
    var config = rule.type === 'voucher' ? select('side', [['all','整張傳票'],['debit','借方'],['credit','貸方']], rule.side) +
      select('quantifier', [['any','至少一筆符合'],['all','全部符合'],['none','不存在符合']], rule.quantifier) +
      '<p>以下條件由同一筆分錄判斷。全部符合須至少有一筆；沒有該側分錄時，只有不存在符合成立。</p>' :
      '<p>括號內的欄位條件綁定同一筆分錄。可再加入括號，組合「且」與「或」。</p>';
    var children = (rule.rules || []).map(function (child, i) {
      var join = i ? '<select data-child-join="' + i + '" aria-label="子條件連接方式"><option value="AND"' + (child.join !== 'OR' ? ' selected' : '') + '>且</option><option value="OR"' + (child.join === 'OR' ? ' selected' : '') + '>或</option></select>' : '';
      return join + ruleRowHtml(child, { matchScope: 'row' }, gi, path + '.' + i, insideVoucher || rule.type === 'voucher');
    }).join('');
    var choices = Ui.FilterValues.groupedFields(Store.getState()).map(function (group) {
      return '<optgroup label="' + Ui.esc(group.label) + '">' + group.fields.map(function (f) { return '<option value="field:' + Ui.esc(f.id) + '">' + Ui.esc(f.label) + '</option>'; }).join('') + '</optgroup>';
    }).join('');
    choices += '<option value="type:accountSide">借方或貸方分類</option><option value="type:manualAuto">人工或自動</option><option value="type:group">條件括號</option>';
    if (rule.type === 'group' && !insideVoucher) { choices += '<option value="type:voucher">傳票量詞</option>'; }
    if (groupMatchScope(outerGroup) === 'sameVoucher') {
      config = '<p>' + (Number(path) === 0 ? '主要條件，決定命中分錄' : '同傳票佐證，括號內欄位必須由同一筆分錄滿足') + '</p>' + config;
    }
    return '<div class="rule-row rule-row--compound" data-gi="' + gi + '" data-ri="' + path + '"><div class="filter-rule-heading"><strong>' +
      (rule.type === 'voucher' ? '傳票條件' : '條件括號') + '</strong><button type="button" class="rule-row__remove" data-compound-remove>移除</button></div>' + config + children +
      '<label>加入子條件<select data-child-kind>' + choices + '</select></label><button type="button" class="btn btn--ghost btn--tiny" data-child-add>加入</button></div>';
  }

  // 每個家族的「對象」下拉：欄位（含借貸別、人工／自動）、科目看哪一側、風險樣態是哪一種。
  // fieldValue 的欄位下拉由 FilterValues 自己畫（帶同一組分錄性質選項），這裡不重複。
  function ruleSubjectHtml(rule, family) {
    var state = Store.getState();
    if (family === 'field' && rule.type !== 'fieldValue') {
      return '';
    }
    if (family === 'account') {
      if (rule.type === 'accountPair' || rule.type === 'specialAccountCategoryPair') { return ''; }
      var current = rule.type === 'accountSide' ? (rule.drCr === 'credit' ? 'credit' : 'debit') : 'pair';
      return '<label>看<select data-account-subject>' + [
        { value: 'debit', label: '借方科目' }, { value: 'credit', label: '貸方科目' }, { value: 'pair', label: '借貸組合' }
      ].map(function (item) {
        return '<option value="' + item.value + '"' + (item.value === current ? ' selected' : '') + '>' + item.label + '</option>';
      }).join('') + '</select></label>';
    }
    if (family === 'pattern') {
      var available = availableRuleTypes();
      var selected = rule.type === 'prescreen' ? 'prescreen:' + rule.prescreenKey : rule.type;
      function option(value, label) { return '<option value="' + value + '"' + (value === selected ? ' selected' : '') + '>' + Ui.esc(label) + '</option>'; }
      var kct = available.filter(function (t) { return t.group === 'kct'; });
      // 左邊的家族小標已經寫「樣態」，下拉不再重複一次標題。
      return '<select data-pattern-subject aria-label="樣態">' +
        '<optgroup label="風險訊號">' + availablePrescreenKeys().map(function (o) { return option('prescreen:' + o.value, o.label); }).join('') + '</optgroup>' +
        '<optgroup label="自訂條件">' + PATTERN_PARAM_TYPES.map(function (t) { return option(t.value, t.label); }).join('') + '</optgroup>' +
        (kct.length ? '<optgroup label="KCT 專屬">' + kct.map(function (t) { return option(t.value, t.label); }).join('') + '</optgroup>' : '') +
      '</select>';
    }
    return '';
  }

  function bindCompoundRule(row, siblings, index, gi) {
    var rule = siblings[index];
    function own(selector) { return Array.from(row.querySelectorAll(selector)).filter(function (el) { return el.closest('.rule-row') === row; }); }
    function changed() { Store.setFilterDraft(Store.getState().filter.draft); }
    own('[data-compound-key]').forEach(function (select) { select.addEventListener('change', function () { rule[select.getAttribute('data-compound-key')] = select.value; changed(); }); });
    own('[data-child-join]').forEach(function (select) { select.addEventListener('change', function () { rule.rules[Number(select.getAttribute('data-child-join'))].join = select.value; changed(); }); });
    own('[data-compound-remove]')[0].addEventListener('click', function () { siblings.splice(index, 1); changed(); });
    own('[data-child-add]')[0].addEventListener('click', function () {
      var value = own('[data-child-kind]')[0].value;
      var fresh;
      if (value.indexOf('type:') === 0) { fresh = Ui.newFilterRule(value.slice(5)); }
      else {
        var f = Ui.FilterValues.fields(Store.getState()).find(function (field) { return field.id === value.slice(6); });
        if (!f) { return; }
        fresh = Ui.FilterValues.create(f.type);
        delete fresh.field; delete fresh.fieldId;
        fresh[f.extra ? 'fieldId' : 'field'] = f.id;
        if (f.extra && f.type === 'money') { fresh.amountBasis = 'signed'; }
      }
      fresh.join = 'AND'; rule.rules.push(fresh); changed();
    });
  }

  function legacyRulePolicyHtml(rule) {
    var note = '';
    if ((rule.type === 'text' || rule.type === 'textSet') && ['notContains', 'notExact'].indexOf(rule.mode) >= 0) {
      note = '此文字排除沿用原設定：保留空白值。';
    } else if (rule.type === 'typed' && ['notEquals', 'notContains', 'notIn'].indexOf(rule.operator) >= 0) {
      note = '此額外欄位條件沿用原設定：不納入空白值。';
    } else if (rule.type === 'accountPair' || rule.type === 'specialAccountCategoryPair') {
      note = '範本分類留白的科目視為 Others；未在配對檔的科目不屬於任何分類。';
    }
    return note ? '<details class="filter-rule-notes"><summary>判定說明</summary><p class="rule-field__hint">' + Ui.esc(note) + '</p></details>' : '';
  }

  // 規則上某一側目前選取的分類身分。帶陣列時陣列即權威；只有 legacy 單選定義才把
  // scalar 讀成「該內建分類的單元素集合」，與系統端的相容規則一致。
  function ruleCategoryIds(rule, idsKey, legacyKey) {
    if (Array.isArray(rule[idsKey])) { return rule[idsKey]; }
    var legacyId = Ui.builtInCategoryIdForLegacyLabel(rule[legacyKey]);
    return legacyId ? [legacyId] : [];
  }

  function categoryIdsLabel(state, ids) {
    return (ids || []).map(function (id) {
      return Ui.taxonomyCategoryLabel(state, id);
    }).join(Ui.FILTER_CATEGORY_LIST_SEPARATOR);
  }

  function categorySelectionHtml(rule) {
    return '<label>選取依據<select data-rule-bind="categorySelection">' + [['role','相同審計角色'],['node','僅分類本身'],['subtree','分類及所有下層']].map(function (item) {
      return '<option value="' + item[0] + '"' + ((rule.categorySelection || 'role') === item[0] ? ' selected' : '') + '>' + item[1] + '</option>';
    }).join('') + '</select></label>';
  }

  function ruleControlsHtml(rule, gi, ri) {
    function fieldSelect(keys) {
      return '<select data-rule-bind="field">' + keys.map(function (key) {
        return '<option value="' + key + '"' + (rule.field === key ? ' selected' : '') + '>' +
          Ui.esc(Ui.glFieldLabel(key)) + '</option>';
      }).join('') + '</select>';
    }

    // 分類多選：每一側送出 taxonomy 的分類身分陣列。同一 fieldset 內以 checkbox 呈現，
    // 去重與順序無關（選兩個同類型與只選其中一個等價，判定只看類型）。
    function categoryMultiSelect(idsKey, legacyKey, legend) {
      var state = Store.getState();
      var selected = ruleCategoryIds(rule, idsKey, legacyKey);
      var boxes = Ui.taxonomyTree(state).map(function (category, index) {
        var id = 'cat-' + gi + '-' + ri + '-' + idsKey + '-' + index;
        return '<label class="category-option" for="' + id + '">' +
          '<input type="checkbox" id="' + id + '" data-category-bind="' + idsKey + '"' +
            ' value="' + Ui.esc(category.categoryId) + '"' +
            (selected.indexOf(category.categoryId) >= 0 ? ' checked' : '') + '>' +
          '<span>' + Ui.esc('　'.repeat(category.depth) + (category.depth ? '└ ' : '') + category.label) + '</span>' +
        '</label>';
      }).join('');
      return '<fieldset class="category-select">' +
        '<legend class="category-select__legend">' + Ui.esc(legend) + '</legend>' +
        boxes +
      '</fieldset>';
    }

    switch (rule.type) {
      case 'fieldValue':
        return Ui.FilterValues.render(rule, Store.getState(), 'value-' + gi + '-' + ri, PSEUDO_FIELDS);
      case 'accountSide':
        return categorySelectionHtml(rule) + '<select data-rule-bind="categoryMode" aria-label="科目條件">' + Ui.ACCOUNT_SIDE_MODE_OPTIONS.map(function (item) {
            return '<option value="' + item.value + '"' + (rule.categoryMode === item.value ? ' selected' : '') + '>' + item.label + '</option>';
          }).join('') + '</select>' +
          categoryMultiSelect('categoryIds', 'category', '指定分類') +
          '<p class="rule-field__hint">範本分類留白的科目視為 Others；未在配對檔的科目不屬於任何分類。「整張傳票」模式輸出整張傳票的分錄，其餘模式只輸出符合的那一列。</p>';
      case 'prescreen':
        return '';

      case 'text':
        return fieldSelect(Ui.FILTER_TEXT_FIELDS) +
          '<input type="text" data-rule-bind="keywords" placeholder="以逗號分隔多個關鍵字" value="' +
            Ui.esc(rule.keywords) + '">' +
          '<select data-rule-bind="mode">' + Ui.TEXT_MODE_OPTIONS.map(function (o) {
            return '<option value="' + o.value + '"' + (rule.mode === o.value ? ' selected' : '') + '>' +
              o.label + '</option>';
          }).join('') + '</select>';

      case 'textSet': {
        var values = Array.isArray(rule.values) ? rule.values : [];
        var helpId = 'text-set-values-help-' + gi + '-' + ri;
        return fieldSelect(Ui.FILTER_TEXT_FIELDS) +
          '<span class="rule-field rule-field--values">' +
            '<textarea rows="3" data-rule-bind="values" aria-describedby="' + helpId + '">' +
              Ui.esc(values.join('\n')) + '</textarea>' +
            '<span class="rule-field__hint" id="' + helpId + '">每行一個值（最多 ' +
              Ui.TEXT_SET_MAX_VALUES + ' 個）</span>' +
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
          '<input type="date" data-rule-bind="from" value="' + Ui.esc(rule.from) + '">' +
          '<span class="rule-row__sep">～</span>' +
          '<input type="date" data-rule-bind="to" value="' + Ui.esc(rule.to) + '">';

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
          '<option value="true"' + (rule.isManual !== 'false' ? ' selected' : '') + '>人工分錄</option>' +
          '<option value="false"' + (rule.isManual === 'false' ? ' selected' : '') + '>自動分錄</option>' +
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
          (showDebit ? categoryMultiSelect('debitCategoryIds', 'debitCategory', rule.pairMode === 'notDrCr' ? '整張傳票不得有這些借方分類' : '借方分類') : '') +
          (showCredit ? categoryMultiSelect('creditCategoryIds', 'creditCategory', rule.pairMode === 'drNotCr' ? '整張傳票不得有這些貸方分類' : '貸方分類') : '') +
          '<p class="filter-pair-result">' + (rule.pairMode === 'drNotCr' ? '結果列出符合的借方分錄。' : rule.pairMode === 'notDrCr' ? '結果列出符合的貸方分錄。' :
            rule.pairMode === 'debitAnchor' ? '結果列出符合的借方分錄及同傳票全部貸方分錄。' : rule.pairMode === 'creditAnchor' ? '結果列出符合的貸方分錄及同傳票全部借方分錄。' : '結果列出同傳票符合分類的借方與貸方分錄。') + '</p></div>';
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
          return '<option value="' + Ui.esc(f.fieldId) + '"' +
            (rule.fieldId === f.fieldId ? ' selected' : '') + '>' + Ui.esc(f.label) + '</option>';
        }).join('');
        var typedHtml = '<select data-rule-bind="fieldId">' + fieldOptions + '</select>';

        if (!valueType) {
          return typedHtml + '<span class="rule-row__field-label">請先選擇欄位</span>';
        }

        typedHtml += '<select data-rule-bind="operator">' +
          '<option value="">（請選擇比較方式）</option>' +
          operators.map(function (o) {
            return '<option value="' + o.value + '"' + (rule.operator === o.value ? ' selected' : '') + '>' +
              o.label + '</option>';
          }).join('') + '</select>';

        var inputType = valueType === 'date' ? 'date' : (valueType === 'money' ? 'number' : 'text');
        if (carrier === 'value' && rule.operator) {
          typedHtml += '<input type="' + inputType + '" data-rule-bind="value" value="' +
            Ui.esc(rule.value) + '">';
        } else if (carrier === 'range') {
          typedHtml += '<input type="' + inputType + '" data-rule-bind="from" value="' + Ui.esc(rule.from) + '">' +
            '<span class="rule-row__sep">～</span>' +
            '<input type="' + inputType + '" data-rule-bind="to" value="' + Ui.esc(rule.to) + '">';
        } else if (carrier === 'set') {
          var typedValues = Array.isArray(rule.values) ? rule.values : [];
          typedHtml += '<span class="rule-field rule-field--values">' +
            '<textarea rows="3" data-rule-bind="values" aria-describedby="' + typedHelpId + '">' +
              Ui.esc(typedValues.join('\n')) + '</textarea>' +
            '<span class="rule-field__hint" id="' + typedHelpId + '">每行一個值（最多 ' +
              Ui.TYPED_SET_MAX_VALUES + ' 個）</span>' +
          '</span>';
        }

        if (valueType === 'money' && carrier !== 'none') {
          typedHtml += '<select data-rule-bind="amountBasis">' +
            Ui.TYPED_AMOUNT_BASIS_OPTIONS.map(function (o) {
              return '<option value="' + o.value + '"' +
                (rule.amountBasis === o.value ? ' selected' : '') + '>' + o.label + '</option>';
            }).join('') + '</select>';
        }

        return typedHtml;
      }

      case 'customKeywords':
        return '<span class="rule-row__field-label">摘要含</span>' +
          '<input type="text" data-rule-bind="keywords" placeholder="以逗號分隔多個關鍵字" value="' +
            Ui.esc(rule.keywords) + '">';

      case 'customTrailingZeros':
        return '<input type="number" data-rule-bind="digits" min="1" max="12" step="1" aria-label="尾數連續 0 的位數" value="' +
            Ui.esc(rule.digits) + '">' +
          '<span class="rule-row__sep" title="金額整數部分末尾連續為 0 的位數；小數不計，整數部分為 0 不列入">位</span>';

      case 'customPreparerEntryCount':
      case 'customAccountEntryCount':
        return '<input type="number" data-rule-bind="maxEntries" min="1" step="1" aria-label="分錄筆數上限" value="' +
            Ui.esc(rule.maxEntries) + '">' +
          '<span class="rule-row__sep">筆</span>';

      case 'entityFrequency': {
        function countSelect(key, label, items) {
          return '<label>' + label + '<select data-rule-bind="' + key + '">' + items.map(function (item) {
            return '<option value="' + item[0] + '"' + (rule[key] === item[0] ? ' selected' : '') + '>' + Ui.esc(item[1]) + '</option>';
          }).join('') + '</select></label>';
        }
        return countSelect('field', '統計對象', ['accNum', 'createBy', 'approveBy'].map(function (key) { return [key, Ui.glFieldLabel(key)]; })) +
          countSelect('countUnit', '計算單位', [['entries', '分錄筆數'], ['vouchers', '去重傳票張數']]) +
          countSelect('countOperator', '比較方式', [['equals', '等於'], ['lessThan', '小於'], ['greaterThan', '大於'], ['between', '介於'],
            ['lessThanOrEqual', '小於或等於'], ['greaterThanOrEqual', '大於或等於']]) +
          '<input type="number" data-rule-bind="countFrom" min="0" step="1" aria-label="統計次數或區間下限" value="' + Ui.esc(rule.countFrom) + '">' +
          (rule.countOperator === 'between' ? '<span>至</span><input type="number" data-rule-bind="countTo" min="0" step="1" aria-label="統計區間上限" value="' + Ui.esc(rule.countTo) + '"><span>含兩個端點</span>' : '') +
          '<p class="rule-explanation">在本情境選定的母體內，依匯入後的科目或人員識別值分組；空白識別值不命中。' +
          (rule.countUnit === 'vouchers' ? '同案件相同傳票號碼算一張，空白號碼不計張；符合該科目或人員的分錄仍會列出。' : '同張傳票有多列時，每列各計一筆。') + '</p>';
      }

      case 'revenueDebitNearQuarterEnd':
        return '<span class="rule-row__field-label" title="總帳日期落在曆年季末前指定天數內的收入借方分錄">季末前</span>' +
          '<input type="number" data-rule-bind="windowDays" min="1" max="92" step="1" placeholder="天數" value="' +
            Ui.esc(rule.windowDays) + '">' +
          '<span class="rule-row__sep">天內借記收入</span>';

      case 'revenueWithoutNormalCounterpart':
        return '<span class="rule-row__field-label" title="貸方為收入，但同一張傳票沒有應收或預收的借方分錄">貸方為收入，借方非應收或預收</span>';

      case 'manualRevenueEntry':
        return '<span class="rule-row__field-label" title="科目為收入且為人工分錄">收入的人工分錄</span>';

      case 'trailingDigits':
        // 範例提示改常駐 helper text（NN/g、GOV.UK：hover-only/title 對鍵盤/觸控/報讀器不友善；
        // 欄位有預設值 000000，placeholder 不可用，故範例放欄位下方常駐一行）。
        return '<span class="rule-row__field-label" title="金額整數部分的末尾數字符合任一指定內容，小數不計">金額尾數為</span>' +
          '<span class="rule-field">' +
            '<input type="text" data-rule-bind="keywords" value="' + Ui.esc(rule.keywords) + '">' +
            '<span class="rule-field__hint">例：999999 或 000000</span>' +
          '</span>';

      case 'preparerEqualsApprover':
        return '<span class="rule-row__field-label" title="同一張傳票的建立人員與核准人員相同">編製＝核准同一人</span>';

      default:
        return '';
    }
  }

  function isExplicitVoucherCondition(rule) {
    return rule.type === 'voucher' || rule.type === 'group' && (rule.rules || []).length > 0 && rule.rules.every(isExplicitVoucherCondition);
  }
  function containsExplicitVoucherCondition(rule) {
    return rule.type === 'voucher' || (rule.rules || []).some(containsExplicitVoucherCondition);
  }
  function ruleSummaryLabel(rule, index, compact) {
    var prefix = index === 0 ? '' : (effectiveRuleJoin(rule) === 'OR' ? '或 ' : '且 ');
    if ((rule.type === 'accountPair' || rule.type === 'specialAccountCategoryPair') && rule.categorySelection) {
      prefix += { role: '相同審計角色：', node: '僅分類本身：', subtree: '包含下層分類：' }[rule.categorySelection] || '';
    }
    switch (rule.type) {
      case 'group':
      case 'voucher': {
        var expr = '';
        (rule.rules || []).forEach(function (child) {
          var atom = ruleSummaryLabel(child, 0, compact);
          expr = expr ? '（' + expr + (effectiveRuleJoin(child) === 'OR' ? ' 或 ' : ' 且 ') + atom + '）' : atom;
        });
        var head = rule.type === 'voucher' ? ({ all: '整張傳票', debit: '借方', credit: '貸方' }[rule.side] || '') +
          ({ any: '至少一筆符合', all: '全部符合（至少有一筆）', none: '不存在符合' }[rule.quantifier] || '') + '：' : '';
        if (rule.type === 'voucher') { return prefix + head + (rule.rules.length === 1 ? expr : '同一分錄（' + expr + '）'); }
        var scope = rule.rules.length > 0 && rule.rules.every(isExplicitVoucherCondition) ? '同張傳票'
          : rule.rules.some(containsExplicitVoucherCondition) ? '條件組合' : '同一分錄';
        return prefix + scope + '（' + expr + '）';
      }
      case 'fieldValue':
        return prefix + Ui.FilterValues.summary(rule, Store.getState(), compact);
      case 'accountSide': {
        var side = rule.drCr === 'credit' ? '貸方' : '借方';
        var categories = categoryIdsLabel(Store.getState(), rule.categoryIds);
        categories += { node: '（僅分類本身）', subtree: '（包含下層分類）', role: '（相同審計角色）' }[rule.categorySelection] || '';
        if (rule.categoryMode === 'is') { return prefix + side + '科目屬於「' + categories + '」'; }
        if (rule.categoryMode === 'isNot') { return prefix + side + '科目不屬於「' + categories + '」'; }
        if (rule.categoryMode === 'absent') { return prefix + '整張傳票的' + side + '都不屬於「' + categories + '」'; }
        return prefix + side + '尚未選擇分類條件';
      }
      case 'prescreen': {
        var hit = Ui.PRESCREEN_KEY_OPTIONS.filter(function (o) { return o.value === rule.prescreenKey; })[0];
        return prefix + '預篩選：' + (hit ? hit.label : rule.prescreenKey);
      }
      case 'text': {
        var mode = Ui.TEXT_MODE_OPTIONS.filter(function (o) { return o.value === rule.mode; })[0];
        return prefix + Ui.glFieldLabel(rule.field) + ' ' + (mode ? mode.label : rule.mode) +
          '「' + rule.keywords + '」';
      }
      case 'textSet': {
        var textSetMode = Ui.TEXT_SET_MODE_OPTIONS.filter(function (o) { return o.value === rule.mode; })[0];
        var textSetNormalization = Ui.TEXT_SET_NORMALIZATION_OPTIONS.filter(function (o) {
          return o.value === rule.normalization;
        })[0];
        var textSetValues = Array.isArray(rule.values) ? rule.values : [];
        return prefix + Ui.glFieldLabel(rule.field) + ' ' +
          (textSetMode ? textSetMode.label : Ui.TEXT_SET_MODE_OPTIONS[0].label) +
          '「' + textSetValues.join('、') + '」（' +
          (textSetNormalization ? textSetNormalization.label : Ui.TEXT_SET_NORMALIZATION_OPTIONS[0].label) + '）';
      }
      case 'dateRange':
        return prefix + Ui.glFieldLabel(rule.field) + ' ' + (rule.from || '…') + '～' + (rule.to || '…');
      case 'numRange':
        return prefix + '金額（絕對值） ' + (rule.from ? '≥ ' + rule.from : '') +
          (rule.from && rule.to ? '、' : '') + (rule.to ? '≤ ' + rule.to : '');
      case 'drCrOnly':
        return prefix + (rule.drCr === 'credit' ? '僅貸方' : '僅借方');
      case 'manualAuto':
        return prefix + (rule.isManual === 'false' ? '自動分錄' : '人工分錄');
      case 'accountPair': {
        var pairMode = Ui.ACCOUNT_PAIR_MODE_OPTIONS.filter(function (o) { return o.value === rule.pairMode; })[0];
        var pairState = Store.getState();
        var pairDebit = categoryIdsLabel(pairState, ruleCategoryIds(rule, 'debitCategoryIds', 'debitCategory'));
        var pairCredit = categoryIdsLabel(pairState, ruleCategoryIds(rule, 'creditCategoryIds', 'creditCategory'));
        var pairDetail = rule.pairMode === 'debitAnchor'
          ? '借方 ' + pairDebit
          : (rule.pairMode === 'creditAnchor'
            ? '貸方 ' + pairCredit
            : '借方 ' + pairDebit + '，貸方 ' + pairCredit);
        return prefix + '借貸科目組合：' + (pairMode ? pairMode.label : rule.pairMode) + '（' + pairDetail + '）';
      }
      case 'specialAccountCategoryPair': {
        var specialMode = Ui.SPECIAL_PAIR_MODE_OPTIONS.filter(function (o) { return o.value === rule.pairMode; })[0];
        var specialState = Store.getState();
        return prefix + '借貸科目組合：' + (specialMode ? specialMode.label : rule.pairMode) + '（借方 ' +
          categoryIdsLabel(specialState, ruleCategoryIds(rule, 'debitCategoryIds', 'debitCategory')) +
          '，貸方 ' +
          categoryIdsLabel(specialState, ruleCategoryIds(rule, 'creditCategoryIds', 'creditCategory')) + '）';
      }
      case 'typed': {
        // 讀回逐字對齊系統端 renderer：欄位以目前顯示名稱呈現（欄位已移除時退回原識別字），
        // operand 依 carrier 呈現原始輸入值，金額條件附比較基準。
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
        if (rdeFieldValueType(rule.fieldId) === 'money' && typedCarrier !== 'none') {
          var basis = Ui.TYPED_AMOUNT_BASIS_OPTIONS.filter(function (o) {
            return o.value === rule.amountBasis;
          })[0];
          typedBasis = basis ? '（' + basis.label + '）' : '';
        }
        return prefix + typedLabel + ' ' + typedOp + typedOperand + typedBasis;
      }
      case 'customKeywords':
        return prefix + '自訂關鍵字「' + rule.keywords + '」';
      case 'customTrailingZeros':
        return prefix + '尾數連續 ' + rule.digits + ' 個 0';
      case 'customPreparerEntryCount':
        return prefix + '所選母體內編製人員分錄筆數 ≤ ' + rule.maxEntries;
      case 'customAccountEntryCount':
        return prefix + '所選母體內科目分錄筆數 ≤ ' + rule.maxEntries;
      case 'entityFrequency':
        return prefix + '所選母體內「' + Ui.glFieldLabel(rule.field) + '」' +
          (rule.countUnit === 'vouchers' ? '去重傳票張數' : '分錄筆數') + ' ' +
          ({ equals: '等於', lessThan: '小於', greaterThan: '大於', between: '介於', lessThanOrEqual: '小於或等於', greaterThanOrEqual: '大於或等於' }[rule.countOperator] || '') +
          ' ' + rule.countFrom + (rule.countOperator === 'between' ? '～' + rule.countTo + '（含端點）' : '');
      case 'revenueDebitNearQuarterEnd':
        return prefix + '季末前 ' + (rule.windowDays || '…') + ' 天借記收入';
      case 'revenueWithoutNormalCounterpart':
        return prefix + '貸方為收入，借方非應收或預收';
      case 'manualRevenueEntry':
        return prefix + '收入之人工分錄';
      case 'trailingDigits': {
        // 讀回講清楚「先捨小數取整數、再比末 k 位」；須與後端 FilterConditionRenderer.TrailingDigitsAtom 逐字相同。
        var tdAtoms = String(rule.keywords || '').split(',').map(function (raw) {
          return raw.trim();
        }).filter(function (p) {
          return p.length > 0;
        }).map(function (p) {
          return '末 ' + p.length + ' 位 = ' + p;
        });
        return prefix + '主單位整數(捨小數)' + tdAtoms.join(' 或 ');
      }
      case 'preparerEqualsApprover':
        return prefix + '編製＝核准同一人';
      default:
        return prefix + rule.type;
    }
  }

  function scenarioPillsHtml(groups) {
    return readBackHtml({ groups: groups || [], __preserveJoins: true });
  }

  function previewPaneHtml(preview) {
    var body;
    if (!preview) {
      var filter = Store.getState().filter;
      var hasConditions = filter.draft.groups.some(function (group) { return group.rules.length > 0; });
      body = '<p class="empty-state">' + (filter.previewExpired && hasConditions
        ? '條件已變更。按「查看符合的傳票」更新結果。'
        : '選好條件後，按「查看符合的傳票」。') + '</p>';
    } else if (preview.voucherPage) {
      body = Ui.FilterVouchers.summary(preview);
    } else {
      body =
        '<p class="rule-card__sub">命中 ' + Number(preview.count).toLocaleString() + ' 筆／' +
          Number(preview.voucherCount).toLocaleString() + ' 張傳票；以下為前 ' +
          preview.previewRows.length + ' 筆預覽。測試母體：' +
          Ui.esc(populationScopeLabel(preview.populationScope)) + '。</p>' +
        '<div class="preview-table__wrap">' + Ui.previewTableHtml(preview.previewRows) + '</div>';
    }

    // 內容區獨立節點＋data-empty 標記：供值編輯時軟失效抽換（softExpirePreviewPane），不重建整面板。
    return (
      '<section class="rule-card">' +
        '<h3 class="rule-card__title" data-preview-title tabindex="-1">預覽結果</h3>' +
        '<div data-bind="preview-pane-body"' + (preview ? '' : ' data-empty="1"') + '>' + body + '</div>' +
      '</section>'
    );
  }

  function savedScenariosHtml(state) {
    var saved = state.filter.savedScenarios;
    var committed = committedPopulationScope(state);
    var items = saved.length === 0
      ? '<p class="empty-state">尚未保存情境。請到「篩選與檢視」設定條件並保存。</p>'
      : saved.map(function (s, i) {
          // 展開狀態取自 viewState：草稿編輯的 bump 重繪不得把已展開的詳情收回（內容由
          // restoreViewState 以快取回填，不重抓）。
          var open = !!viewState.openScenarios[i];
          var confirmingRemoval = viewState.pendingRemovalIndex === i;
          return (
            '<article class="saved-scenario">' +
              '<div class="saved-scenario__head">' +
                '<span class="saved-scenario__name">' + (i + 1) + '. ' + Ui.esc(s.name) + '</span>' +
                '<button type="button" class="btn btn--ghost" data-action="toggle-scenario" data-index="' + i +
                  '">' + (open ? '收起結果' : '查看結果') + '</button>' +
                '<details class="filter-saved-actions"><summary>操作</summary><div>' +
                '<button type="button" class="btn btn--ghost" data-action="edit-scenario" data-index="' + i + '">編輯</button>' +
                '<button type="button" class="btn btn--ghost" data-action="copy-scenario" data-index="' + i + '">另存副本</button>' +
                '<button type="button" class="btn btn--ghost" data-action="remove-scenario" data-index="' + i +
                  '"' + (confirmingRemoval ? ' hidden' : '') + '>移除</button></div></details>' +
              '</div>' +
              (confirmingRemoval
                ? '<div class="saved-scenario__confirm" role="group" aria-label="確認移除篩選情境">' +
                    '<p>將移除此情境，並需重新產生條件篩選報告與底稿</p>' +
                    '<div class="saved-scenario__confirm-actions">' +
                      '<button type="button" class="btn btn--ghost" data-action="cancel-remove-scenario" ' +
                        'data-index="' + i + '">取消</button>' +
                      '<button type="button" class="btn btn--danger" data-action="confirm-remove-scenario" ' +
                        'data-index="' + i + '">確認移除</button>' +
                    '</div>' +
                  '</div>'
                : '') +
              '<div class="saved-scenario__body" data-bind="scenario-body-' + i + '"' + (open ? '' : ' hidden') + '>' +
                '<p class="rule-card__sub">' + Ui.esc(s.rationale) + '</p>' +
                '<div class="scenario-pills">' + scenarioPillsHtml(s.groups) + '</div>' +
                '<div class="saved-scenario__preview" data-bind="scenario-preview-' + i + '"></div>' +
              '</div>' +
            '</article>'
          );
        }).join('');

    return (
      '<section class="rule-card">' +
        '<h3 class="rule-card__title" data-bind="saved-scenarios-title" tabindex="-1">已儲存篩選情境' +
          '<span class="check-item__count">' + saved.length + ' / 10</span></h3>' +
        (committed
          ? '<p class="rule-card__sub">已保存版本的測試母體：' + Ui.esc(populationScopeLabel(committed)) + '。</p>'
          : (saved.length > 0
            ? '<p class="form-notice">這批情境來自較早的版本。請在上方「測試母體」按「以查核期間重新保存」；保存完成後才會顯示矩陣、完整命中和報告。</p>'
            : '')) +
        items +
      '</section>'
    );
  }

  function criteriaReportHtml(state) {
    var resultRef = state.filterResultRef;
    var committedScope = committedPopulationScope(state);
    var validationRunId = state.lastRuns.validate && state.lastRuns.validate.resultRef
      ? state.lastRuns.validate.resultRef.runId : null;
    var artifact = committedScope && resultRef && validationRunId
      ? Ui.findCurrentReportArtifact(state, 'criteriaSelectionReport', {
          validationRunId: validationRunId,
          scenarioRevision: resultRef.revision
        })
      : null;
    var canExport = !!committedScope && !!validationRunId &&
      state.filter.savedScenarios.length > 0;
    // 只要曾經產生過報告或上游資料已變更，按鈕就叫「重新產生」，和第六步提示裡指的按鈕名稱一致。
    var regenerate = !!artifact || (state.staleState && state.staleState.filter) ||
      Ui.reportArtifactHistory(state, 'criteriaSelectionReport').length > 0;
    return (
      '<section class="report-output">' +
        '<div class="report-output__head">' +
          '<div>' +
            '<h3 class="report-output__title">條件篩選報告</h3>' +
            '<p class="report-output__hint">報告採用已保存的情境。條件或資料變更後，請重新產生。</p>' +
          '</div>' +
          '<button type="button" class="btn" data-action="export-criteria-report"' +
            (canExport ? '' : ' disabled') + '>' +
            (regenerate ? '重新產生條件篩選報告' : '完成條件篩選並產生報告') +
          '</button>' +
        '</div>' +
        Ui.reportArtifactListHtml(artifact ? [artifact] : [],
          canExport ? '尚未產生目前版本的報告。' :
            (state.filter.savedScenarios.length > 0 && !committedPopulationScope(state)
              ? '已保存的情境來自較早的版本，請先在上方「測試母體」按「以查核期間重新保存」。'
              : '請先完成目前版本的資料驗證並保存至少一個篩選情境。')) +
      '</section>'
    );
  }

  /* ---- 高風險條件矩陣（D2 預覽：step3 摘要 + step4 傳票矩陣 + step4-1 行層） ----
     全程零商業邏輯：只把 wire 回來的 matchedPositions 對映成 C 欄 ✓／空白、組表；
     查詢/pivot/計算/SQL 全在後端三 action（tagMatrixScenarios/VoucherPage/RowPage）。 */

  // 摺疊外殼；展開時惰性載入(bind() 的 toggle 處理)。無已存情境時不顯示展開鈕,直接友善空狀態。
  function tagMatrixHtml(state) {
    var saved = state.filter.savedScenarios;
    if (saved.length === 0) {
      return (
        '<section class="rule-card">' +
          '<h3 class="rule-card__title">高風險條件矩陣</h3>' +
          '<p class="empty-state">尚未保存任何篩選情境；先在上方保存情境，矩陣會把每個情境當成一個高風險條件欄（C1..CN）。</p>' +
        '</section>'
      );
    }

    if (!committedPopulationScope(state)) {
      return (
        '<section class="rule-card">' +
          '<h3 class="rule-card__title">高風險條件矩陣</h3>' +
          '<p class="form-notice">已保存的情境來自較早的版本。請先在上方「測試母體」按「以查核期間重新保存」，矩陣才會顯示。</p>' +
        '</section>'
      );
    }

    // 展開狀態取自 viewState（跨 bump 重繪保留）；內容由 restoreViewState 以摘要快取回填。
    var open = viewState.matrixOpen;
    return (
      '<section class="rule-card">' +
        '<div class="rule-card__head">' +
          '<h3 class="rule-card__title">高風險條件矩陣</h3>' +
          '<button type="button" class="btn btn--ghost" data-action="toggle-matrix">' +
            (open ? '收合矩陣' : '展開矩陣') + '</button>' +
        '</div>' +
        '<p class="rule-card__sub">展開後可交叉檢視各情境的命中摘要、傳票與分錄明細。</p>' +
        '<div class="saved-scenario__body" data-bind="matrix-body"' + (open ? '' : ' hidden') + '>' +
          '<nav class="filter-matrix-tabs" aria-label="矩陣檢視">' +
            matrixViewButton('scenarios', '命中摘要') + matrixViewButton('vouchers', '傳票交叉表') + matrixViewButton('rows', '分錄明細') + '</nav>' +
          '<div data-bind="matrix-scenarios"></div>' +
          '<div data-bind="matrix-vouchers"></div>' +
          '<div data-bind="matrix-rows"></div>' +
        '</div>' +
      '</section>'
    );
  }

  // 由情境摘要組「C 欄定義」：依 position 升冪,每欄 { position, label:'C{p}', name }。
  // C 欄欄序與標頭一律以此為準(brief：依 tagMatrixScenarios 回的 position 升冪)。
  function buildTagColumns(scenarios) {
    return (scenarios || [])
      .slice()
      .sort(function (a, b) { return a.position - b.position; })
      .map(function (s) {
        return { position: s.position, label: 'C' + s.position, name: s.name };
      });
  }

  // 情境摘要表(step3 交叉參考)：位置/條件名稱(=C 欄)/傳票命中數/行命中數。
  function scenarioSummaryTableHtml(columns) {
    var rows = columns.map(function (c) {
      var s = c.summary;
      return (
        '<tr>' +
          '<td>' + Ui.esc(c.label) + '</td>' +
          '<td>' + Ui.esc(s.name) + '</td>' +
          '<td class="preview-table__amount">' + Number(s.voucherHitCount).toLocaleString() + '</td>' +
          '<td class="preview-table__amount">' + Number(s.rowHitCount).toLocaleString() + '</td>' +
        '</tr>'
      );
    }).join('');

    return (
      '<h4 class="rule-card__title">情境摘要</h4>' +
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr><th>欄</th><th>高風險條件（情境名稱）</th><th>命中傳票數</th><th>命中行數</th></tr></thead>' +
          '<tbody>' + rows + '</tbody>' +
        '</table>' +
      '</div>'
    );
  }

  // 動態 C 欄的標頭片段:C{p},以情境名稱當 tooltip(title)。
  function tagColumnHeadHtml(columns) {
    return columns.map(function (c) {
      return '<th title="' + Ui.esc(c.name) + '">' + Ui.esc(c.label) + '<span class="filter-matrix-name">' + Ui.esc(c.name) + '</span></th>';
    }).join('');
  }

  // 把 matchedPositions(陣列)對映成每個 C 欄的 ✓／空白 column 取值函式(供 appendRowsToTbody)。
  // 純對映:matchedPositions 含該欄 position → '✓',否則空白。不做任何查詢/計算。
  function tagCellColumns(columns) {
    return columns.map(function (c) {
      return { cell: function (row) {
        var hit = (row.matchedPositions || []).indexOf(c.position) >= 0;
        return hit ? '✓' : '';
      }, className: 'preview-table__tag' };
    });
  }

  function paneButton(key, label) {
    return '<button type="button" data-filter-pane-select="' + key + '" aria-pressed="' +
      (viewState.workspacePane === key) + '">' + Ui.esc(label) + '</button>';
  }

  function sourceButton(key, label) {
    return '<button type="button" data-condition-source="' + key + '" aria-pressed="' +
      (viewState.conditionSource === key) + '">' + Ui.esc(label) + '</button>';
  }

  function conditionTargetHtml(draft) {
    var groups = draft.groups.map(function (group, index) { return { group: group, index: index }; })
      .filter(function (item) { return !item.group.__kctPresetGroup; });
    if (groups.length < 2) { return ''; }
    var active = activeEditableGroup(draft);
    return '<label class="filter-target">新增條件到<select class="form__input" data-condition-target>' +
      groups.map(function (item, position) { return '<option value="' + item.index + '"' +
        (item.group === active ? ' selected' : '') + '>第 ' + (position + 1) + ' 組</option>'; }).join('') + '</select></label>';
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
      '<div class="panel panel--wide filter-workspace">' +
        '<h2 class="panel__title">進階條件篩選</h2>' +
        '<p class="panel__hint">設定篩選條件並保存成情境；保存前可先查看符合的傳票。</p>' +
        Ui.mappingReviewBannerHtml(state) +
        Ui.staleNoticeHtml(state, 'filter', '資料已變更，情境條件仍保留。請到「已保存情境與矩陣」，按「重新產生條件篩選報告」會用目前資料重新計算，之後才能到第六步匯出。') +
        populationScopeHtml(state) +
        '<nav class="filter-work-tabs" aria-label="篩選工作區">' +
          paneButton('filter', '篩選與檢視') + paneButton('saved', '已保存情境與矩陣（' + saved.length + '）') + '</nav>' +
        '<section data-filter-pane="filter"' + (viewState.workspacePane === 'filter' ? '' : ' hidden') + '>' +
          '<div class="filter-workbench"><aside class="filter-entry" tabindex="-1" aria-label="加入篩選條件">' +
            '<div class="filter-entry-heading"><h3 class="filter-entry-title">條件總覽</h3></div>' + conditionTargetHtml(draft) +
            '<div class="filter-source-tabs" aria-label="條件來源">' + sourceButton('kct', 'KCT條件') + sourceButton('custom', '自訂篩選條件') + '</div>' +
            '<div class="filter-entry-scroll" data-preserve-scroll="filter-condition-catalog">' +
            '<div data-condition-pane="kct"' + (viewState.conditionSource === 'kct' ? '' : ' hidden') + '>' + kctPickerHtml(draft) + '</div>' +
            '<div data-condition-pane="custom"' + (viewState.conditionSource === 'custom' ? '' : ' hidden') + '>' + customPickerHtml() + '</div>' +
            '<button type="button" class="btn btn--ghost filter-data-reference" data-action="preview-population">查看全部分錄</button>' +
            templatePickerHtml(state.importState) +
          '</div></aside><div class="filter-current">' + scenarioBuilderHtml(draft) + previewPaneHtml(state.filter.preview) + '</div></div>' +
        '</section>' +
        '<section data-filter-pane="saved"' + (viewState.workspacePane === 'saved' ? '' : ' hidden') + '>' +
          savedScenariosHtml(state) + tagMatrixHtml(state) + criteriaReportHtml(state) + '</section>' +
        Ui.stepFooterHtml(state) + '</div>';

    bind(container);
    restoreViewState(container);
  }

  // bump 全重繪後回填「已展開」惰性面板的內容（hidden 與按鈕文字已在 HTML 生成時還原；這裡補內容：
  // 已存情境詳情與矩陣用 viewState 快取回填、無快取才重抓——見 ensureScenarioPreview／ensureMatrixContent）。
  function restoreViewState(container) {
    var draftPreview = Store.getState().filter.preview;
    if (draftPreview && draftPreview.voucherPage) {
      Ui.FilterVouchers.mount(container.querySelector('[data-bind="preview-pane-body"] .filter-voucher-host'), draftPreview);
    }
    if (viewState.workspacePane === 'saved') { restoreSavedViews(container); }
  }

  function restoreSavedViews(container) {
    Object.keys(viewState.openScenarios).forEach(function (k) {
      if (viewState.openScenarios[k]) { ensureScenarioPreview(container, Number(k)); }
    });
    if (viewState.matrixOpen) { ensureMatrixContent(container); }
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

  // 攸關資料元素條件的值清單上限鏡像（manifest 1–100）；後端仍是權威。
  function hasOversizedTypedSet(draft) {
    return draft.groups.some(function (group) {
      return group.rules.some(function (rule) {
        return rule.type === 'typed' && Array.isArray(rule.values) &&
          rule.values.length > Ui.TYPED_SET_MAX_VALUES;
      });
    });
  }

  // 預覽／保存前的前端引導：名稱、動機或至少一條件未補齊時，於欄位旁（紅框＋紅字）
  // 與按鈕上方（form-notice）就近提示並不送出。純呈現——後端 invalid_scenario 仍是權威。
  // 純自訂情境名稱/動機必填；KCT 草稿只要仍有 marker 就可留白，後端保存時產生正準留痕替補。
  function scenarioGate(container, includeSavedScenarios) {
    var draft = Store.getState().filter.draft;
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
    var notice = container.querySelector('[data-bind="scenario-notice"]');

    clearFieldError(nameInput);
    clearFieldError(rationaleInput);

    var nameEmpty = !draft.name || !draft.name.trim();
    var rationaleEmpty = !draft.rationale || !draft.rationale.trim();
    var metadataRequired = includeSavedScenarios && requiresScenarioMetadata(draft);
    var noCondition = (draft.groups.length === 0 ||
      draft.groups.every(function (g) { return g.rules.length === 0; }));
    var oversizedTextSet = hasOversizedTextSet(draft);
    var oversizedTypedSet = hasOversizedTypedSet(draft);
    var incompleteTyped = hasIncompleteTypedRule(draft);

    var problems = [];
    draft.groups.reduce(function (rules, group) { return rules.concat(group.rules); }, [])
      .forEach(function (rule) {
        if (rule.type === 'fieldValue') { var message = Ui.FilterValues.problem(rule, Store.getState()); if (message) { problems.push(message); } }
        if (rule.type === 'dateRange' && rule.from && rule.to && rule.from > rule.to) { problems.push('日期區間的起點晚於終點，請先修正'); }
      });
    if (metadataRequired && nameEmpty) { setFieldError(nameInput, '請先填寫情境名稱'); problems.push('情境名稱'); }
    if (metadataRequired && rationaleEmpty) { setFieldError(rationaleInput, '請先填寫篩選動機說明'); problems.push('篩選動機'); }
    if (noCondition) { problems.push('至少一條篩選條件'); }
    if (oversizedTextSet) {
      problems.push('文字值清單每組最多 ' + Ui.TEXT_SET_MAX_VALUES + ' 個值');
    }
    if (oversizedTypedSet) {
      problems.push('額外欄位的值清單每組最多 ' + Ui.TYPED_SET_MAX_VALUES + ' 個值');
    }
    if (incompleteTyped) {
      problems.push('額外欄位條件需選定欄位與比較方式');
    }

    if (problems.length === 0) {
      if (notice) { notice.hidden = true; notice.textContent = ''; }
      return true;
    }
    if (notice) {
      notice.textContent = '尚需補齊：' + problems.join('、');
      notice.hidden = false;
    }
    // 聚焦第一個有問題的欄位（名稱 → 動機）；只缺條件時不搶焦點。
    if (metadataRequired && nameEmpty) { nameInput.focus(); }
    else if (metadataRequired && rationaleEmpty) { rationaleInput.focus(); }
    return false;
  }

  // 使用者輸入時放寬（NN/g：別在打字途中責備）——只清除已補齊欄位的錯誤、
  // 全部補齊時收起按鈕旁提示；不在此新增任何錯誤。
  function softRefreshGate(container) {
    var draft = Store.getState().filter.draft;
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
    var notice = container.querySelector('[data-bind="scenario-notice"]');
    var metadataRequired = requiresScenarioMetadata(draft);

    if (draft.name && draft.name.trim()) { clearFieldError(nameInput); }
    if (draft.rationale && draft.rationale.trim()) { clearFieldError(rationaleInput); }

    var noCondition = (draft.groups.length === 0 ||
      draft.groups.every(function (g) { return g.rules.length === 0; }));
    var oversizedTextSet = hasOversizedTextSet(draft);
    var stillMissing = (metadataRequired && (!draft.name || !draft.name.trim())) ||
      (metadataRequired && (!draft.rationale || !draft.rationale.trim())) ||
      noCondition || oversizedTextSet || hasOversizedTypedSet(draft) || hasIncompleteTypedRule(draft);
    if (!stillMissing && notice) { notice.hidden = true; notice.textContent = ''; }
  }

  // 規則「值編輯」後即時刷新藍色 read-back：值編輯走 patchFilterRule（只 notify、不重建面板以保住輸入
  // 焦點），故 read-back（整段算好的 HTML）不會自己更新——這裡只抽換 .scenario-readback 這一段，不碰
  // 任何輸入框，焦點不受影響（read-back 是條件清單之後的獨立節點）。
  function softRefreshReadback(container) {
    syncSuggestedMetadata(container);
    var el = container.querySelector('.filter-readback .scenario-readback');
    if (!el) { return; }
    var html = readBackHtml(Store.getState().filter.draft);
    if (html) { el.outerHTML = html; }
    else if (el.parentNode) { el.parentNode.removeChild(el); }
  }

  // 值編輯後把預覽面板軟更新為「已失效」空狀態：patchFilterRule 已把 Store 的預覽清掉（值編輯改變
  // 命中集合），但它只 notify 不重繪（保住輸入焦點），畫面上的舊命中數不會自己消失——這裡只抽換
  // 預覽面板的內容區，不碰任何輸入框。已是空狀態時直接返回（冪等，連續打字不重複動 DOM、不抖動）。
  function softExpirePreviewPane(container) {
    var body = container.querySelector('[data-bind="preview-pane-body"]');
    if (!body || body.getAttribute('data-empty') === '1') { return; }
    body.innerHTML = '<p class="empty-state">條件已變更。按「查看符合的傳票」更新結果。</p>';
    body.setAttribute('data-empty', '1');
  }

  // 已存情境命中預覽的實體渲染＋「載入更多」接線（詳情展開的惰性載入與 bump 重繪後的快取還原共用）。
  // s = filter.preview 回傳的 scenario 物件；scenarioPosition 對齊後端 1-based position（保存順序），
  // 用於 query.filterHitsPage 接續行層明細。
  function renderScenarioHitPreview(previewEl, s, scenarioPosition, allowLoadMore) {
    if (s.voucherPage) {
      previewEl.innerHTML = Ui.FilterVouchers.summary(s);
      previewEl.setAttribute('data-loaded', '1');
      Ui.FilterVouchers.mount(previewEl.querySelector('.filter-voucher-host'), s);
      return;
    }
    var rows = s.previewRows.slice(0, 10);
    previewEl.innerHTML =
      '<p class="rule-card__sub">命中 ' + Number(s.count).toLocaleString() + ' 筆／' +
        Number(s.voucherCount).toLocaleString() + ' 張傳票，以下為前 ' + rows.length +
        ' 筆。測試母體：' + Ui.esc(populationScopeLabel(s.populationScope)) + '。</p>' +
      '<div class="preview-table__wrap">' + Ui.previewTableHtml(rows) + '</div>' +
      (allowLoadMore
        ? '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more" data-action="hits-load-more">載入更多</button>'
        : '<p class="form-notice">這裡只先列出前幾筆。保存情境後，才能載入全部命中。</p>');
    previewEl.setAttribute('data-loaded', '1');

    if (!allowLoadMore) { return; }

    // 「載入更多」逐頁接續命中行層明細。完整命中的欄位由系統端決定：固定欄之後只附
    // 這個情境實際引用的額外欄位，因此首擊時連同表頭一起換成系統端回傳的欄位定義。
    // 純呼叫膠水:發 query.filterHitsPage、依同一份欄位定義接列、到底移除鈕;畫面不判命中。
    var table = previewEl.querySelector('.preview-table');
    var tbody = previewEl.querySelector('.preview-table tbody');
    var moreBtn = previewEl.querySelector('[data-action="hits-load-more"]');
    var lastPage = null;
    var cells = null;
    Ui.bindPagedTable(previewEl, {
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryFilterHitsPage({
          scenarioPosition: scenarioPosition, cursor: cursor, pageSize: 200, sort: sort || null, search: search || null
        }).then(function (page) {
          lastPage = page;
          return page;
        });
      },
      appendRows: function (hitRows) { Ui.appendRowsToTbody(tbody, hitRows, cells); },
      clearRows: function () {
        // 首擊清掉預覽前 10 列(filter.preview 另一套排序),改接資料庫端的第一頁,避免重複與排序不一致；
        // 同時以系統端欄位定義重建表頭（固定欄可點排序），讓額外欄位與資料列對齊。
        var columns = (lastPage && lastPage.columns) || [];
        cells = Ui.dynamicColumnCells(columns);
        var thead = table ? table.querySelector('thead') : null;
        if (thead) { thead.innerHTML = Ui.dynamicColumnHeadHtml(columns); }
        tbody.innerHTML = '';
      },
      loadMore: moreBtn,
      table: table
    });
  }

  // 確保某個展開中的已存情境詳情有命中預覽：優先用 viewState 快取（bump 重繪後不重抓；已存定義只在
  // commit 時變，跨草稿編輯保留快取是安全的），沒有快取才發 filter.preview 並寫入快取。
  // data-loaded 旗標防同一份 DOM 生命週期內重複載入（重複展開/收合不重抓，行為同改版前）。
  function ensureScenarioPreview(container, index) {
    var previewEl = container.querySelector('[data-bind="scenario-preview-' + index + '"]');
    if (!previewEl || previewEl.getAttribute('data-loaded') === '1') { return; }

    var currentState = Store.getState();
    var committedScope = committedPopulationScope(currentState);
    var populationScope = committedScope || selectedPopulationScope(currentState);
    var allowLoadMore = !!committedScope;

    var scenarioPosition = index + 1;
    // 沒有 committed revision 時，bounded preview 依目前 draft scope 即時計算，不跨 scope 快取。
    var cached = allowLoadMore ? viewState.scenarioPreviews[index] : null;
    if (cached) {
      renderScenarioHitPreview(previewEl, cached, scenarioPosition, true);
      return;
    }

    var savedRef = currentState.filter.savedScenarios;
    var resultRef = currentState.filterResultRef;
    var projectId = currentState.project ? currentState.project.projectId : null;
    var scenario = savedRef[index];
    if (!scenario) { return; }
    var acceptResponse = scenarioResponseGuard(index).issue(function () {
      var latest = Store.getState();
      return !!latest.project && latest.project.projectId === projectId &&
        latest.filter.savedScenarios === savedRef &&
        latest.filterResultRef === resultRef &&
        (allowLoadMore || selectedPopulationScope(latest) === populationScope);
    });

    previewEl.textContent = '載入中…';
    Ui.runBackground('預覽已儲存情境', function () {
      // savedScenarios 保存時即經 toWireScenario 剝乾淨；此處再過一次同一投影，保證送出形狀一致。
      return global.JetApi.filterPreview({
        populationScope: populationScope,
        scenario: toWireScenario(scenario)
      }).then(function (data) {
        var request = allowLoadMore ? { scenarioPosition: scenarioPosition, scenarioRevision: resultRef.revision, populationScope: populationScope }
          : { scenario: toWireScenario(scenario), populationScope: populationScope };
        return global.JetApi.queryFilterVoucherPage(Object.assign({}, request, { pageSize: 50 })).then(function (page) {
          data.scenario.voucherPage = page; data.scenario.voucherRequest = request; return data;
        });
      }).then(function (data) {
        // 回應期間已存清單被替換（commit／移除／resume）：index 對位已變，結果作廢，
        // 不寫入剛被 syncViewState 重置的快取（防止舊定義的預覽掛到新清單的同一序位上）。
        if (!acceptResponse()) { return; }
        if (allowLoadMore) { viewState.scenarioPreviews[index] = data.scenario; }
        renderScenarioHitPreview(previewEl, data.scenario, scenarioPosition, allowLoadMore);
      }).catch(function (error) {
        // 舊案件／舊 revision 的失敗不可污染目前案件訊息；當前失敗則留可重試提示並交給共用訊息區。
        if (!acceptResponse()) { return; }
        previewEl.innerHTML = '<p role="status">載入失敗：' + Ui.esc(error && error.message ? error.message : '請稍後重試。') + '</p>' +
          '<p>已保存的情境設定仍保留。' + (error && error.code === 'invalid_scenario'
            ? '請依原因確認欄位配對或編輯情境。修改來源欄位後，請重新驗證，再回來重試。'
            : '可以直接重試讀取結果。') + '</p>' +
          '<button type="button" class="btn btn--ghost" data-scenario-retry>重試讀取結果</button>' +
          (error && error.code === 'invalid_scenario' ?
            '<button type="button" class="btn btn--ghost" data-scenario-return-mapping>返回欄位配對</button>' : '');
        previewEl.querySelector('[data-scenario-retry]').addEventListener('click', function () {
          ensureScenarioPreview(container, index);
        });
        var mappingButton = previewEl.querySelector('[data-scenario-return-mapping]');
        if (mappingButton) { mappingButton.addEventListener('click', function () { Ui.gotoStep(2); }); }
        throw error;
      });
    });
  }

  // 會改變同一列可見控制項組合的規則鍵：改動後必須重繪，否則畫面會停在舊的輸入形狀。
  var STRUCTURAL_RULE_KEYS = ['fieldId', 'operator', 'pairMode', 'categoryMode', 'countUnit', 'countOperator'];

  function bindLegacyPicker(container) {
    container.querySelector('[data-filter-disclosure=legacy-form]').addEventListener('toggle', function (event) { viewState.disclosures['legacy-form'] = event.target.open; });
    container.querySelector('[data-legacy-letter]').addEventListener('change', function (event) {
      viewState.legacyLetter = event.target.value; viewState.legacyField = '';
      viewState.disclosures['legacy-form'] = true;
      container.querySelector('[data-legacy-host]').innerHTML = legacyPickerHtml();
      bindLegacyPicker(container);
      container.querySelector('[data-legacy-letter]').focus({ preventScroll: true });
    });
    var legacyField = container.querySelector('[data-legacy-field]');
    if (legacyField) { legacyField.addEventListener('change', function () { viewState.legacyField = legacyField.value; }); }
    container.querySelector('[data-action="add-legacy-rule"]').addEventListener('click', function () {
      var item = global.JetLegacyFilters.catalogue.conditions.find(function (entry) { return entry.letter === viewState.legacyLetter; });
      var state = Store.getState(), draft = state.filter.draft;
      var field = legacyField && Ui.FilterValues.fields(state).find(function (entry) { return entry.id === legacyField.value; });
      var rules = global.JetLegacyFilters.rules(item, field);
      var account = container.querySelector('[data-legacy-account]');
      if (account && account.value === 'categories') { rules = global.JetLegacyFilters.rules({ rules: item.categoryRules }); }
      var person = container.querySelector('[data-legacy-person]');
      if (person && person.value !== 'frequency') { rules = [{ type: 'fieldValue', field: person.value, operator: 'in', values: [], includeBlank: false }]; }
      var group = activeEditableGroup(draft);
      if (!group) { group = { join: 'AND', matchScope: 'row', rules: [] }; draft.groups.push(group); setActiveGroup(draft, group); }
      rules.forEach(function (rule) { rule.join = groupCombinator(group); group.rules.push(rule); });
      viewState.disclosures['legacy-form'] = true;
      Store.setFilterDraft(draft); revealAddedRule(rules[rules.length - 1]);
    });
    container.querySelectorAll('[data-legacy-example]').forEach(function (button) {
      button.addEventListener('click', function () {
        var example = global.JetLegacyFilters.catalogue.examples.find(function (entry) { return entry.key === button.dataset.legacyExample; });
        var rules = global.JetLegacyFilters.rules(example);
        if (example.key === 'example2') {
          var chosen = container.querySelector('[data-legacy-example-field]').value;
          if (!chosen) { container.querySelector('[data-legacy-notice]').textContent = '範例 2 需要部門等額外文字欄位。請先選取；沒有選項時，回欄位配對加入來源。'; return; }
          rules[1].fieldId = chosen;
        }
        var draft = { name: example.combination + ' ' + example.label, rationale: example.rationale,
          groups: [{ join: 'AND', matchScope: 'row', rules: rules }], __nameDirty: true, __rationaleDirty: true };
        viewState.saveOpen = false; viewState.disclosures['legacy-form'] = true;
        Store.setFilterDraft(draft); revealAddedRule(rules[0]);
      });
    });
  }

  function bind(container) {
    syncSuggestedMetadata(container);
    Ui.bindStepFooter(container);
    container.querySelectorAll('.filter-saved-actions').forEach(function (menu) {
      menu.addEventListener('keydown', function (event) { if (event.key === 'Escape') { menu.open = false; menu.querySelector('summary').focus(); } });
    });
    container.querySelectorAll('[data-filter-pane-select]').forEach(function (button) {
      button.addEventListener('click', function () {
        viewState.workspacePane = button.dataset.filterPaneSelect;
        container.querySelectorAll('[data-filter-pane]').forEach(function (pane) { pane.hidden = pane.dataset.filterPane !== viewState.workspacePane; });
        container.querySelectorAll('[data-filter-pane-select]').forEach(function (tab) { tab.setAttribute('aria-pressed', tab === button ? 'true' : 'false'); });
        if (viewState.workspacePane === 'saved') { restoreSavedViews(container); }
      });
    });
    container.querySelectorAll('[data-condition-source]').forEach(function (button) {
      button.addEventListener('click', function () {
        viewState.conditionSource = button.dataset.conditionSource;
        container.querySelectorAll('[data-condition-pane]').forEach(function (pane) { pane.hidden = pane.dataset.conditionPane !== viewState.conditionSource; });
        container.querySelectorAll('[data-condition-source]').forEach(function (tab) { tab.setAttribute('aria-pressed', tab === button ? 'true' : 'false'); });
      });
    });
    container.querySelectorAll('[data-filter-disclosure]').forEach(function (details) {
      details.addEventListener('toggle', function () { viewState.disclosures[details.dataset.filterDisclosure] = details.open; });
    });
    var customSubject = container.querySelector('[data-custom-subject]');
    bindLegacyPicker(container);
    customSubject.addEventListener('change', function () {
      viewState.customSubject = customSubject.value;
      container.querySelector('[data-action="add-rule"]').disabled = !customSubject.value || !!accountMappingRequirementNote(customSubject.value.slice(5));
    });
    var conditionTarget = container.querySelector('[data-condition-target]');
    if (conditionTarget) {
      conditionTarget.addEventListener('change', function () { chooseTargetGroup(Number(conditionTarget.value), true); });
    }
    function setSaveOpen(open) {
      viewState.saveOpen = open;
      syncSuggestedMetadata(container);
      softRefreshGate(container);
      container.querySelector('[data-save-panel]').hidden = !open;
      container.querySelector('.filter-primary-actions').hidden = open;
      container.querySelector('[data-action="open-save"]').setAttribute('aria-expanded', String(open));
      var target = container.querySelector(open ? '[data-bind="scenario-name"]' : '[data-action="open-save"]');
      target.focus();
    }
    container.querySelector('[data-action="open-save"]').addEventListener('click', function () { setSaveOpen(true); });
    container.querySelector('[data-action="close-save"]').addEventListener('click', function () { setSaveOpen(false); });
    // 只同步加入目標及其呈現，不重建正在輸入的條件，也不移動焦點或捲軸。
    function chooseTargetGroup(gi, clearSelection) {
      var draft = Store.getState().filter.draft;
      setActiveGroup(draft, draft.groups[gi]);
      if (clearSelection) {
        selectedRule = null;
        container.querySelectorAll('.rule-row--selected').forEach(function (row) { row.classList.remove('rule-row--selected'); });
        container.querySelectorAll('.filter-rule-selected').forEach(function (badge) { badge.remove(); });
      }
      if (conditionTarget) { conditionTarget.value = String(gi); }
      container.querySelectorAll('[data-group-index]').forEach(function (well) {
        var active = Number(well.dataset.groupIndex) === gi;
        well.classList.toggle('scenario-set--active', active);
      });
      // 沿用原本的 KCT 呈現函式；保留按鈕節點與事件，僅更新該組的勾選及「也在其他組」。
      var template = document.createElement('template');
      template.innerHTML = kctPickerHtml(draft);
      container.querySelectorAll('.filter-entry [data-kct-letter]').forEach(function (button) {
        var updated = template.content.querySelector('[data-kct-letter="' + button.dataset.kctLetter + '"]');
        button.className = updated.className;
        button.setAttribute('aria-pressed', updated.getAttribute('aria-pressed'));
        button.innerHTML = updated.innerHTML;
      });
      container.querySelector('.condition-picker__count').textContent = template.content.querySelector('.condition-picker__count').textContent;
    }
    container.querySelectorAll('[data-action="apply-template"]').forEach(function (button) {
      button.addEventListener('click', function () { applyTemplate(button.getAttribute('data-template-key')); });
    });
    function editScenario(index, copy) {
      var state = Store.getState(), saved = state.filter.savedScenarios[index];
      if (!saved) { return; }
      var draft = JSON.parse(JSON.stringify(saved));
      draft.__preserveJoins = true;
      if (draft.editorOrigins) {
        draft.__restoredOrigins = true;
        draft.__legacyKctSource = !!draft.editorOrigins.legacyKctSource;
        draft.groups.forEach(function (group, gi) {
          var origin = draft.editorOrigins.groups[gi];
          group.__kctPresetGroup = !!origin.presetGroup;
          group.rules.forEach(function (rule, ri) { if (origin.letters[ri]) { rule[KCT_LETTER_KEY] = origin.letters[ri]; } });
        });
      } else if (draft.source === 'kct') { draft.__legacyKctSource = true; }
      setActiveGroup(draft, draft.groups.find(function (group) { return !group.__kctPresetGroup; }));
      draft.__nameDirty = true; draft.__rationaleDirty = true;
      if (!copy) { draft.__editingIndex = index; draft.__editingSavedRef = state.filter.savedScenarios; }
      else {
        var copyBaseName = draft.name || '篩選情境', copyNumber = 1;
        do { draft.name = copyBaseName + '（副本' + (copyNumber === 1 ? '' : ' ' + copyNumber) + '）'; copyNumber++; }
        while ((Store.getState().filter.savedScenarios || []).some(function (item) { return item.name === draft.name; }));
      }
      viewState.workspacePane = 'filter'; viewState.saveOpen = false;
      Store.setFilterDraft(draft);
      if (global.JetFocus) { global.JetFocus.defer(function () { return document.querySelector('.filter-workspace [data-builder-title]'); }); }
    }
    container.querySelectorAll('[data-action="edit-scenario"], [data-action="copy-scenario"]').forEach(function (button) {
      button.addEventListener('click', function () { editScenario(Number(button.dataset.index), button.dataset.action === 'copy-scenario'); });
    });
    var cancelEdit = container.querySelector('[data-action="cancel-edit-scenario"]');
    if (cancelEdit) { cancelEdit.addEventListener('click', function () {
      viewState.saveOpen = false; viewState.disclosures = {};
      Store.setFilterDraft({ name: '', rationale: '', groups: [] });
    }); }
    var copyEdit = container.querySelector('[data-action="save-scenario-copy"]');
    if (copyEdit) { copyEdit.addEventListener('click', function () {
      saveScenario(true);
    }); }

    var resaveButton = container.querySelector('[data-action="resave-scenarios"]');
    if (resaveButton) {
      resaveButton.addEventListener('click', function () {
        Ui.run('以目前母體重新保存篩選情境', function () {
          var state = Store.getState();
          var populationScope = selectedPopulationScope(state);
          var scenarios = state.filter.savedScenarios.map(toWireScenario);
          if (scenarios.length === 0) { return Promise.resolve(); }
          return global.JetApi.filterCommit({
            populationScope: populationScope,
            scenarios: scenarios
          }).then(function (data) {
            var canonicalScope = data.resultRef.populationScope;
            Store.setFilterResultRef(data.resultRef);
            Store.setSavedScenarios(data.scenarios);
            Store.addMessage('已用「' + populationScopeLabel(canonicalScope) + '」重新保存 ' +
              data.savedCount + ' 個篩選情境。', 'info');
          });
        }, { logCompletion: true });
      });
    }

    var criteriaBtn = container.querySelector('[data-action="export-criteria-report"]');
    if (criteriaBtn) {
      criteriaBtn.addEventListener('click', function () {
        var state = Store.getState();
        var resultRef = state.filterResultRef;
        var validationRunId = state.lastRuns.validate && state.lastRuns.validate.resultRef
          ? state.lastRuns.validate.resultRef.runId : null;
        if (!committedPopulationScope(state) || !resultRef || !resultRef.revision ||
            !validationRunId) { return; }
        Ui.run('完成條件篩選並產生報告', function () {
          return global.JetApi.exportCriteriaSelectionReport({
            validationRunId: validationRunId,
            revision: resultRef.revision
          })
            .then(function (data) {
              Store.upsertReportArtifacts([data.artifact]);
              Store.addMessage('已在專案目錄產生條件篩選報告，可進入匯出底稿。', 'info');
              return data;
            });
        }, { logCompletion: true });
      });
    }

    // 名稱／動機：值編輯只 patch（notify），不重建面板（保住輸入焦點）。
    // dirty 旗標：非空手改＝鎖住 KCT 自動命名對該欄的覆寫；清空（含只剩空白）＝交還自動命名（applyKctNaming）。
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    nameInput.addEventListener('input', function () {
      Store.patchFilterDraftMeta({ name: nameInput.value, __nameDirty: !!nameInput.value.trim() });
      softRefreshGate(container);
    });
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
    rationaleInput.addEventListener('input', function () {
      Store.patchFilterDraftMeta({ rationale: rationaleInput.value, __rationaleDirty: !!rationaleInput.value.trim() });
      softRefreshGate(container);
    });

    // 組內的「＋」：新增一條該家族的條件到按鈕所在的那一組（data-gi）；那一組還不存在（新草稿的空第 1 組）
    // 就先建組。新規則 join 取該組現有組合器以維持群組內一致；被加的組同時成為 KCT 卡的落點。
    container.querySelectorAll('[data-action="add-rule"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var newRule = customSubjectRule();
        if (!newRule) { return; }
        var gi = btn.hasAttribute('data-gi') ? Number(btn.getAttribute('data-gi')) : NaN;
        var target = draft.groups[gi] && !draft.groups[gi].__kctPresetGroup ? draft.groups[gi] : activeEditableGroup(draft);
        if (!target) {
          target = { join: 'AND', matchScope: 'row', rules: [] };
          draft.groups.push(target);
        }
        setActiveGroup(draft, target);
        var rule = newRule;
        rule.join = groupMatchScope(target) === 'sameVoucher'
          ? 'AND'
          : (target.rules.length ? groupCombinator(target) : 'AND');
        target.rules.push(rule);
        revealAddedRule(rule);
        viewState.customSubject = '';
        Store.setFilterDraft(draft);
      });
    });

    // KCT條件卡（A–J）：可複選 toggle —— 已選則移除、未選則加入；累積成同一情境。
    // 停用卡（B/未匯入科目配對）不綁事件。加入/移除/已選偵測全收斂於上方少數深函式。
    container.querySelectorAll('.picker-card--kct').forEach(function (btn) {
      if (btn.disabled) { return; }
      btn.addEventListener('click', function () {
        var item = Ui.FILTER_KCT_CHECKLIST.filter(function (k) {
          return k.letter === btn.getAttribute('data-kct-letter');
        })[0];
        if (!item) { return; }
        var draft = Store.getState().filter.draft;
        if (isKctSelectedActive(draft, item)) {
          // 取消：單規則只從「作用中組」移除（同訊號在別組不動）；預設(I) 是獨立群組、整組移除。
          if (isPresetNewGroup(item)) { removeKctFromDraft(draft, item); }
          else { removeKctFromActiveGroup(draft, item); }
          applyKctNaming(draft);
          Store.setFilterDraft(draft);
          Store.addMessage('已移除 KCT 條件「' + item.label + '」。', 'info');
        } else {
          addKctToDraft(draft, item); // 單規則→作用中組；預設(I)→自成一組
          if (!isPresetNewGroup(item)) {
            var targetGroup = activeEditableGroup(draft);
            var addedRule = targetGroup && targetGroup.rules.find(function (rule) { return rule[KCT_LETTER_KEY] === item.letter; });
            if (addedRule) { revealAddedRule(addedRule); }
          }
          applyKctNaming(draft);
          Store.setFilterDraft(draft);
          Store.addMessage('已加入 KCT 條件「' + item.label + '」。', 'info');
        }
      });
    });

    // 「＋ 另一組條件」：新增一個可編輯群組（一個 set）。新組 join 繼承有效組間運算子——畫面上只有
    // 一個情境層運算子，硬編碼 'OR' 會在使用者已改 AND 時打破「全組一致」不變量（段控顯示 AND、
    // 新組實際帶 OR 送出）。從一組加到第二組時尚無組間運算子，scenarioJoin 回退預設 OR（HubSpot
    // 慣例），與段控首次出現時的顯示一致。只在已有條件時才出現（見 scenarioBuilderHtml），故綁定前判空。
    var addSetBtn = container.querySelector('[data-action="add-set"]');
    if (addSetBtn) {
      addSetBtn.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var fresh = { join: scenarioJoin(draft), matchScope: 'row', rules: [] };
        draft.groups.push(fresh);
        setActiveGroup(draft, fresh); // 新組即新落點：設為作用中
        selectedRule = null;
        Store.setFilterDraft(draft);
        // 新組已成為左側選單的加入位置；在右側直接呈現它，焦點不回到頁首。
        if (global.JetFocus) { global.JetFocus.defer(function () {
          var workbench = document.querySelector('.filter-workbench');
          if (workbench && global.getComputedStyle(workbench).gridTemplateColumns.split(' ').length === 1) {
            return document.querySelector('.filter-workspace [data-condition-target]') || document.querySelector('.filter-workspace [data-condition-source]');
          }
          return document.querySelector('.filter-workspace [data-group-index="' + draft.groups.indexOf(fresh) + '"]');
        }); }
      });
    }

    // 換位到 GL 標準化資料預覽：只供設定條件時參考，不把全投影預覽冒充目前 scope 的命中集合。
    var populationBtn = container.querySelector('[data-action="preview-population"]');
    if (populationBtn && Ui.openDataPreview) {
      populationBtn.addEventListener('click', function () {
        Ui.openDataPreview('glEntries');
      });
    }

    // 「移除這組」：splice 該可編輯群組（一個 set）。移到只剩一組會自動回乾淨單 well（無模式）。
    // 移除作用中組時重新標記作用中（回退到最後一組），確保恆有一個有效落點。
    container.querySelectorAll('[data-action="remove-set"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var gi = Number(btn.getAttribute('data-gi'));
        var removedHasKct = groupHasKct(draft.groups[gi]);
        draft.groups.splice(gi, 1);
        setActiveGroup(draft, activeEditableGroup(draft));
        if (removedHasKct) { applyKctNaming(draft); } // 移除含 KCT 的組才重算命名
        Store.setFilterDraft(draft);
      });
    });

    // 移除原子預設行（如非營業日 I）：splice 整個預設群組、重算 KCT 命名（對應 KCT 卡因 __kctLetter 消失而取消）。
    container.querySelectorAll('[data-action="remove-preset-group"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        draft.groups.splice(Number(btn.getAttribute('data-gi')), 1);
        applyKctNaming(draft);
        Store.setFilterDraft(draft);
      });
    });

    // 自訂篩選條件區塊折疊：切換 body 的 hidden 與 caret（同 toggle-matrix/toggle-scenario 模式，不重建面板）。
    // 狀態同步進 viewState，讓下一次 bump 重繪照原樣還原（customPickerHtml 讀它）。
    var customToggle = container.querySelector('[data-action="toggle-custom-picker"]');
    if (customToggle) {
      customToggle.addEventListener('click', function () {
        var body = container.querySelector('[data-bind="custom-picker-body"]');
        if (!body) { return; }
        body.hidden = !body.hidden;
        viewState.customPickerOpen = !body.hidden;
        customToggle.setAttribute('aria-expanded', body.hidden ? 'false' : 'true');
        var caret = customToggle.querySelector('.condition-picker__toggle-caret');
        if (caret) { caret.textContent = body.hidden ? '▸' : '▾'; }
      });
    }

    // 群組比對範圍：row 維持既有逐列語意；切到 sameVoucher 時立即把所有 rule.join 收斂為 AND，
    // 與停用的 OR 段控及後端 validator 保持一致。這裡只改 AST，不在前端評估條件。
    container.querySelectorAll('[data-group-bind], [data-set-combinator], [data-set-join]').forEach(function (control) {
      var kind = control.hasAttribute('data-set-join') ? 'join' : control.hasAttribute('data-set-combinator') ? 'combinator' : control.dataset.groupBind;
      var groupIndex = control.hasAttribute('data-set-join') ? control.dataset.setJoinIndex : control.dataset.gi;
      control.setAttribute('data-focus-key', 'filter-group-' + groupIndex + '-' + kind);
    });
    container.querySelectorAll('[data-group-bind="matchScope"]').forEach(function (control) {
      control.addEventListener('change', function () {
        var draft = Store.getState().filter.draft;
        var group = draft.groups[Number(control.getAttribute('data-gi'))];
        if (!group) { return; }
        group.matchScope = control.value;
        if (group.matchScope === 'sameVoucher') {
          group.rules.forEach(function (rule) { rule.join = 'AND'; });
        }
        Store.setFilterDraft(draft);
      });
    });

    // 條件組組合器（組標頭的「全部／任一」下拉，每組一個）：設「該可編輯群組(data-gi)」各 rule 的 join。
    // 預設(I) 群組是情境層級、固定 AND（不再隨組合器同步，見 toWireScenario/Option A）。
    container.querySelectorAll('[data-set-combinator]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        var value = radio.value;
        var draft = Store.getState().filter.draft;
        var group = draft.groups[Number(radio.getAttribute('data-gi'))];
        if (!group) { return; }
        if (groupMatchScope(group) === 'sameVoucher' && value === 'OR') { return; }
        group.rules.forEach(function (r) { r.join = value; });
        Store.setFilterDraft(draft);
      });
    });

    // 條件組之間的連接器（下拉）：單一組間運算子——把所有「非預設」群組的 join 設為一致值
    //（預設群組的 join 由上面 sync-presets 管，不在此動）。
    container.querySelectorAll('[data-set-join]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        var value = radio.value;
        var draft = Store.getState().filter.draft;
        if (draft.__preserveJoins) { draft.groups[Number(radio.dataset.setJoinIndex)].join = value; }
        else { draft.groups.forEach(function (g) { if (!g.__kctPresetGroup) { g.join = value; } }); }
        Store.setFilterDraft(draft);
      });
    });

    container.querySelectorAll('.rule-row').forEach(function (row) {
      var gi = Number(row.getAttribute('data-gi'));
      var path = row.getAttribute('data-ri').split('.').map(Number);
      var ri = path.pop();
      var currentRules = Store.getState().filter.draft.groups[gi].rules;
      path.forEach(function (index) { currentRules = currentRules[index].rules; });
      function patchCurrent(patch) {
        Object.assign(currentRules[ri], patch);
        Store.patchFilterRule(gi, path.length ? path[0] : ri, {});
      }
      if (currentRules[ri].type === 'group' || currentRules[ri].type === 'voucher') {
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
      var currentRule = currentRules[ri];
      function selectCurrentRule() {
        if (!row.isConnected || selectedRule === currentRule) { return; }
        var draft = Store.getState().filter.draft;
        if (!draft.groups[gi].__kctPresetGroup && activeEditableGroup(draft) !== draft.groups[gi]) { chooseTargetGroup(gi, false); }
        selectedRule = currentRule;
        container.querySelectorAll('.rule-row--selected').forEach(function (item) { item.classList.remove('rule-row--selected'); });
        container.querySelectorAll('.filter-rule-selected').forEach(function (badge) { badge.remove(); });
        row.classList.add('rule-row--selected');
        var badge = document.createElement('span');
        badge.className = 'filter-rule-selected'; badge.textContent = '選取中';
        row.querySelector('.filter-rule-heading').prepend(badge);
      }
      row.addEventListener('click', selectCurrentRule);
      row.addEventListener('focusin', selectCurrentRule);


      // 換掉這一列的規則（家族內換對象時用）：保留 join；原本是 KCT 卡帶入的列就解除身分並重算命名。
      function replaceRule(build) {
        var draft = Store.getState().filter.draft;
        var old = currentRules[ri];
        var fresh = build(old);
        fresh.join = old.join;
        if (old.categorySelection && ['accountSide','accountPair','specialAccountCategoryPair'].indexOf(fresh.type) >= 0) { fresh.categorySelection = old.categorySelection; }
        currentRules[ri] = fresh;
        if (selectedRule === old) { selectedRule = fresh; }
        if (old[KCT_LETTER_KEY]) { applyKctNaming(draft); }
        Store.setFilterDraft(draft);
      }
      // 欄位下拉選到「分錄性質」或一般欄位時，換成對應的規則型別。
      function switchFieldSubject(id) {
        if (id === '__drCr') { replaceRule(function () { return Ui.newFilterRule('drCrOnly'); }); return; }
        if (id === '__isManual') { replaceRule(function () { return Ui.newFilterRule('manualAuto'); }); return; }
        var selected = Ui.FilterValues.fields(Store.getState()).find(function (item) { return item.id === id; });
        if (!selected) { return; }
        replaceRule(function () {
          var fresh = Ui.FilterValues.create(selected.type);
          delete fresh.field; delete fresh.fieldId;
          fresh[selected.extra ? 'fieldId' : 'field'] = selected.id;
          if (selected.extra && selected.type === 'money') { fresh.amountBasis = 'signed'; }
          return fresh;
        });
      }

      Ui.FilterValues.bind(row.querySelector('.value-editor'), currentRule, Store.getState(), function (structural, pseudoId) {
        if (structural === 'pseudo') { switchFieldSubject(pseudoId); }
        else if (structural) { Store.setFilterDraft(Store.getState().filter.draft); }
        else { patchCurrent({}); softRefreshReadback(container); softExpirePreviewPane(container); softRefreshGate(container); }
      });
      var fieldSubject = row.querySelector('[data-field-subject]');
      if (fieldSubject) { fieldSubject.addEventListener('change', function () { switchFieldSubject(fieldSubject.value); }); }

      // 科目：借方／貸方是單邊條件（accountSide），借貸組合是配對條件；換邊時把已選分類帶過去。
      var accountSubject = row.querySelector('[data-account-subject]');
      if (accountSubject) {
        accountSubject.addEventListener('change', function () {
          var value = accountSubject.value;
          var old = currentRules[ri];
          if (value === 'pair') {
            if (old.type !== 'accountSide') { return; }
            replaceRule(function () {
              var fresh = Ui.newFilterRule('specialAccountCategoryPair');
              var ids = (old.categoryIds || []).slice();
              if (ids.length) { fresh[old.drCr === 'credit' ? 'creditCategoryIds' : 'debitCategoryIds'] = ids; }
              return fresh;
            });
            return;
          }
          if (old.type === 'accountSide') { patchCurrent({ drCr: value }); Store.touch(); return; }
          replaceRule(function () {
            var fresh = Ui.newFilterRule('accountSide');
            fresh.drCr = value;
            var ids = ruleCategoryIds(old, value === 'credit' ? 'creditCategoryIds' : 'debitCategoryIds', value === 'credit' ? 'creditCategory' : 'debitCategory');
            if (ids.length) { fresh.categoryIds = ids.slice(); }
            return fresh;
          });
        });
      }

      // 風險樣態：預篩選訊號只換 key；換成尾數、張數或 KCT 專屬條件則重建那一列。
      var patternSubject = row.querySelector('[data-pattern-subject]');
      if (patternSubject) {
        patternSubject.addEventListener('change', function () {
          var value = patternSubject.value;
          var old = currentRules[ri];
          if (value.indexOf('prescreen:') === 0) {
            var key = value.slice('prescreen:'.length);
            if (old.type === 'prescreen') { patchCurrent({ prescreenKey: key }); Store.touch(); return; }
            replaceRule(function () { var fresh = Ui.newFilterRule('prescreen'); fresh.prescreenKey = key; return fresh; });
            return;
          }
          if (value === old.type) { return; }
          replaceRule(function () { return Ui.newFilterRule(value); });
        });
      }
      var primary = row.querySelector('[data-action="make-primary"]');
      if (primary) { primary.addEventListener('click', function () {
        var draft = Store.getState().filter.draft, rules = draft.groups[gi].rules;
        rules.unshift(rules.splice(ri, 1)[0]); Store.setFilterDraft(draft);
      }); }

      row.querySelector('[data-action="remove-rule"]').addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var removed = currentRules[ri];
        var wasKct = !!(removed && removed[KCT_LETTER_KEY]);
        currentRules.splice(ri, 1);
        if (wasKct) { applyKctNaming(draft); } // 移除 KCT 條件才重算命名（與卡片 toggle 一致；純自訂不動手改名）
        Store.setFilterDraft(draft);
      });

      // 分類多選：同一側的所有已勾選身分組成陣列（去重、順序不影響判定）。
      // 陣列存在時即權威，legacy 單選欄位不再參與，故不回寫 scalar。
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
            var fresh = Ui.newFilterRule(parts[0]);
            fresh.join = old.join; fresh.pairMode = parts[1]; fresh.categorySelection = old.categorySelection;
            fresh.debitCategoryIds = (old.debitCategoryIds || []).slice(); fresh.creditCategoryIds = (old.creditCategoryIds || []).slice();
            if (old[KCT_LETTER_KEY]) { fresh[KCT_LETTER_KEY] = old[KCT_LETTER_KEY]; }
            currentRules[ri] = fresh;
        if (selectedRule === old) { selectedRule = fresh; }
            Store.setFilterDraft(draft);
          } else {
            patchCurrent({ pairMode: parts[1] });
            Store.touch();
          }
        });
      }
      row.querySelectorAll('[data-rule-bind]').forEach(function (control) {
        var key = control.getAttribute('data-rule-bind');
        // 值編輯用 input（即時）：patchFilterRule 只 patch、不重建面板（保住焦點），並即時刷新藍色
        // read-back，讓「這個情境會找出…」隨輸入同步更新（修：原本 change 要等下次重繪才更新）。
        // 同時把預覽面板軟更新為失效空狀態——read-back 顯示新值而預覽停留舊命中數是同屏矛盾。
        control.addEventListener('input', function () {
          var patch = {};
          patch[key] = key === 'values' ? control.value.split(/\r?\n/) : control.value;
          patchCurrent(patch);
          // 結構性選擇（額外欄位、比較方式、配對模式）會改變這一列該顯示哪些輸入框，
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

    container.querySelector('[data-action="preview-scenario"]').addEventListener('click', function () {
      if (!scenarioGate(container, false)) { return; }
      Ui.run('預覽篩選情境', function () {
        var previewState = Store.getState();
        var draft = previewState.filter.draft;
        var populationScope = selectedPopulationScope(previewState);
        var draftRevision = Store.getFilterDraftRev();
        var projectId = previewState.project ? previewState.project.projectId : null;
        var acceptResponse = draftResponseGuard.issue(function () {
          var latest = Store.getState();
          return !!latest.project && latest.project.projectId === projectId &&
            draftRevision === Store.getFilterDraftRev() &&
            selectedPopulationScope(latest) === populationScope;
        });
        // 送出剝除標記後的 wire 形狀（draft 內 KCT rule 帶 __kctLetter，絕不可外洩）。
        return global.JetApi.filterPreview({
          populationScope: populationScope,
          scenario: toWireDraft(draft)
        }).then(function (data) {
          if (!acceptResponse()) { return null; }
          var request = { populationScope: populationScope, scenario: toWireDraft(draft) };
          return global.JetApi.queryFilterVoucherPage(Object.assign({}, request, { pageSize: 50 })).then(function (page) {
            data.scenario.voucherPage = page; data.scenario.voucherRequest = request; return data;
          });
        }).then(function (data) {
          if (!data) { return; }
          if (!acceptResponse() || data.scenario.populationScope !== populationScope) { return; }
          Store.setFilterPreview(data.scenario);
          if (global.JetFocus) { global.JetFocus.defer(function () { return document.querySelector('.filter-workspace [data-preview-title]'); }); }
          Store.addMessage('情境預覽：命中 ' + data.scenario.count + ' 筆／' +
            data.scenario.voucherCount + ' 張傳票。', 'info');
        }).catch(function (error) { markRuleErrors(container, error); throw error; });
      }, { logCompletion: true });
    });

    // 後端 invalid_scenario 帶 details（第幾組、第幾條、原因）時，把該列標紅、就地寫原因並捲到第一列；
    // 沒有 details 的錯誤維持 Ui.run 的整段訊息。這只是呈現，判定權威仍在後端。
    function markRuleErrors(root, error) {
      root.querySelectorAll('.rule-row--invalid').forEach(function (row) {
        row.classList.remove('rule-row--invalid');
        var old = row.querySelector('[data-rule-error]'); if (old) { old.remove(); }
      });
      if (!error || !Array.isArray(error.details)) { return; }
      var first = null;
      error.details.forEach(function (detail) {
        if (!detail || typeof detail.group !== 'number' || typeof detail.rule !== 'number') { return; }
        var row = root.querySelector('.rule-row[data-gi="' + (detail.group - 1) + '"][data-ri="' + (detail.rule - 1) + '"]');
        if (!row) { return; }
        row.classList.add('rule-row--invalid');
        var note = document.createElement('p');
        note.className = 'rule-row__error'; note.setAttribute('data-rule-error', ''); note.setAttribute('role', 'alert');
        note.textContent = detail.message;
        row.appendChild(note);
        if (!first) { first = row; }
      });
      if (first) { first.scrollIntoView({ block: 'center' }); }
    }

    function saveScenario(asCopy) {
      syncSuggestedMetadata(container);
      if (!scenarioGate(container, true)) { return; }
      Ui.run('保存篩選情境', function () {
        var commitState = Store.getState();
        var current = commitState.filter;
        var populationScope = selectedPopulationScope(commitState);

        // 已存情境 + 當前草稿都過同一投影（深拷貝剝除 __kctLetter，並保留 canonical source:'kct'）。
        // 存入 savedScenarios 的也是這份剝乾淨的形狀，故後續惰性預覽其 groups 不含任何 UI-only 標記。
        var scenarios = current.savedScenarios.map(toWireScenario);
        var editingIndex = asCopy ? null : current.draft.__editingIndex;
        var authored = toWireDraft(current.draft);
        if (asCopy) {
          var suffix = 1, originalName = authored.name;
          do { authored.name = originalName + '（副本' + (suffix === 1 ? '' : ' ' + suffix) + '）'; suffix++; }
          while (scenarios.some(function (scenario) { return scenario.name === authored.name; }));
        }
        if (typeof editingIndex === 'number') {
          if (current.draft.__editingSavedRef !== current.savedScenarios) {
            Store.addMessage('已保存清單已變更，請重新選擇要編輯的情境；目前草稿仍保留。', 'warn');
            return Promise.resolve();
          }
          scenarios[editingIndex] = authored;
        } else { scenarios.push(authored); }
        if (scenarios.length > 10) {
          Store.addMessage('最多保存 10 個篩選情境；請先移除既有情境。', 'warn');
          return Promise.resolve();
        }
        return global.JetApi.filterCommit({
          populationScope: populationScope,
          scenarios: scenarios
        }).then(function (data) {
          viewState.workspacePane = 'saved'; viewState.saveOpen = false;
          viewState.disclosures = {};
          Store.setFilterResultRef(data.resultRef);
          Store.setSavedScenarios(data.scenarios);
          // 保存成功即把草稿重置回初始空狀態：commit 是 replace-all，草稿留著再按一次〔保存〕就會把
          // 同名情境重複帶進 payload 而被後端 invalid_scenario 拒絕。重置走 setFilterDraft（清預覽、
          // 重繪），dirty 旗標與作用中組隨舊 draft 物件一起消失，KCT 卡高亮由空草稿重新推導。必填紅框
          // 不會出現：提示只在使用者按〔預覽〕／〔保存〕當下由 scenarioGate 觸發，重繪即回乾淨表單。
          Store.setFilterDraft({ name: '', rationale: '', groups: [] });
          Store.addMessage('已保存篩選情境（' + data.savedCount + ' / 10）。', 'info');
        }).catch(function (error) { markRuleErrors(container, error); throw error; });
      }, { logCompletion: true });
    }
    container.querySelector('[data-action="save-scenario"]').addEventListener('click', function () { saveScenario(false); });

    container.querySelectorAll('[data-action="toggle-scenario"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var index = Number(btn.getAttribute('data-index'));
        var body = container.querySelector('[data-bind="scenario-body-' + index + '"]');
        if (!body) { return; }

        body.hidden = !body.hidden;
        btn.textContent = body.hidden ? '查看結果' : '收起結果';
        viewState.openScenarios[index] = !body.hidden; // 跨 bump 重繪保留展開狀態
        if (body.hidden) { return; }

        // 首次展開時惰性載入該情境的命中預覽（重用既有 filter.preview，顯示前 10 筆）；
        // 快取與還原邏輯收斂在 ensureScenarioPreview。
        ensureScenarioPreview(container, index);
      });
    });

    container.querySelectorAll('[data-action="remove-scenario"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var index = Number(btn.getAttribute('data-index'));
        viewState.pendingRemovalIndex = index;
        render(container, Store.getState());
        if (global.JetFocus) {
          global.JetFocus.defer(function () {
            return container.querySelector(
              '[data-action="cancel-remove-scenario"][data-index="' + index + '"]');
          });
        }
      });
    });

    container.querySelectorAll('[data-action="cancel-remove-scenario"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var index = Number(btn.getAttribute('data-index'));
        viewState.pendingRemovalIndex = null;
        render(container, Store.getState());
        restoreScenarioRemovalFocus(container, index);
      });
    });

    container.querySelectorAll('[data-action="confirm-remove-scenario"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var index = Number(btn.getAttribute('data-index'));
        var removalSucceeded = false;
        Ui.run('移除篩選情境', function () {
          var removeState = Store.getState();
          var remaining = removeState.filter.savedScenarios
            .filter(function (_, i) { return i !== index; })
            .map(toWireScenario);
          var populationScope = selectedPopulationScope(removeState);
          return global.JetApi.filterCommit({
            populationScope: populationScope,
            scenarios: remaining
          }).then(function (data) {
            viewState.pendingRemovalIndex = null;
            Store.setFilterResultRef(remaining.length > 0 ? data.resultRef : null);
            Store.setSavedScenarios(data.scenarios);
            Store.addMessage('已移除情境（剩餘 ' + remaining.length + ' / 10）。', 'info');
            removalSucceeded = true;
          });
        }, {
          onError: function () {
            if (global.JetFocus) {
              global.JetFocus.defer(function () {
                return container.querySelector(
                  '[data-action="confirm-remove-scenario"][data-index="' + index + '"]');
              });
            }
          }
        }).then(function () {
          if (removalSucceeded) { restoreScenarioRemovalFocus(container, index); }
        });
      });
    });

    bindTagMatrix(container);
  }

  // 高風險條件矩陣:展開時惰性載入(摘要 → 組 C 欄定義 → 傳票矩陣 + 行層,各自載入更多)。
  // 以 matrix-body 的 data-loaded 旗標當快取,重複展開不重抓。零商業邏輯:只發 action、組表、對映 ✓。
  function matrixViewButton(key, label) {
    return '<button type="button" data-matrix-view="' + key + '" aria-pressed="' + (viewState.matrixView === key) + '">' + label + '</button>';
  }

  function showMatrixView(body) {
    ['scenarios', 'vouchers', 'rows'].forEach(function (key) {
      var box = body.querySelector('[data-bind="matrix-' + key + '"]');
      box.hidden = key !== viewState.matrixView;
      if (box.hidden || !body._matrixColumns || box.dataset.rendered) { return; }
      if (key === 'scenarios') { box.innerHTML = scenarioSummaryTableHtml(body._matrixColumns); }
      else if (key === 'vouchers') { renderVoucherMatrix(box, body._matrixColumns); }
      else { renderRowMatrix(box, body._matrixColumns); }
      box.dataset.rendered = '1';
    });
    body.querySelectorAll('[data-matrix-view]').forEach(function (button) {
      button.setAttribute('aria-pressed', String(button.dataset.matrixView === viewState.matrixView));
    });
  }

  function bindTagMatrix(container) {
    container.querySelectorAll('[data-matrix-view]').forEach(function (button) {
      button.addEventListener('click', function () {
        viewState.matrixView = button.dataset.matrixView;
        showMatrixView(container.querySelector('[data-bind="matrix-body"]'));
      });
    });
    var toggle = container.querySelector('[data-action="toggle-matrix"]');
    if (!toggle) { return; }

    toggle.addEventListener('click', function () {
      var body = container.querySelector('[data-bind="matrix-body"]');
      if (!body) { return; }

      body.hidden = !body.hidden;
      toggle.textContent = body.hidden ? '展開矩陣' : '收合矩陣';
      viewState.matrixOpen = !body.hidden; // 跨 bump 重繪保留展開狀態
      if (body.hidden) { return; }

      ensureMatrixContent(container);
    });
  }

  // 矩陣內容實體渲染（展開惰性載入與 bump 重繪後的快取還原共用）。scenarios 為 queryTagMatrixScenarios
  // 回傳的摘要陣列；傳票/行層矩陣的「載入更多」自 cursor null 重新接起（分頁列不跨重繪保留）。
  function renderMatrixContent(body, scenarios) {
    var scenariosBox = body.querySelector('[data-bind="matrix-scenarios"]');
    var vouchersBox = body.querySelector('[data-bind="matrix-vouchers"]');
    var rowsBox = body.querySelector('[data-bind="matrix-rows"]');

    var columns = buildTagColumns(scenarios || []);
    // 把摘要掛回欄定義供摘要表顯示名稱與命中數。
    columns.forEach(function (c) {
      c.summary = (scenarios || []).filter(function (s) { return s.position === c.position; })[0];
    });

    if (columns.length === 0) {
      scenariosBox.innerHTML =
        '<p class="empty-state">尚無可用情境;請先在上方保存篩選情境。</p>';
      return;
    }

    body._matrixColumns = columns;
    showMatrixView(body);
    body.setAttribute('data-loaded', '1');
  }

  // 確保展開中的矩陣有內容：優先用 viewState 摘要快取（草稿編輯不影響已存情境命中，跨重繪保留安全；
  // savedScenarios 替換時由 syncViewState 重置），沒有快取才發 queryTagMatrixScenarios 並寫入。
  function ensureMatrixContent(container) {
    var body = container.querySelector('[data-bind="matrix-body"]');
    if (!body || body.getAttribute('data-loaded') === '1') { return; }
    if (!committedPopulationScope(Store.getState())) {
      body.innerHTML = '<p class="form-notice">已保存的情境來自較早的版本，請先在上方「測試母體」按「以查核期間重新保存」。</p>';
      body.setAttribute('data-loaded', '1');
      return;
    }

    if (viewState.matrixData) {
      renderMatrixContent(body, viewState.matrixData);
      return;
    }

    var scenariosBox = body.querySelector('[data-bind="matrix-scenarios"]');
    scenariosBox.textContent = '載入中…';

    var requestState = Store.getState();
    var savedRef = requestState.filter.savedScenarios;
    var resultRef = requestState.filterResultRef;
    var projectId = requestState.project ? requestState.project.projectId : null;
    var acceptResponse = matrixResponseGuard.issue(function () {
      var latest = Store.getState();
      return !!latest.project && latest.project.projectId === projectId &&
        latest.filter.savedScenarios === savedRef && latest.filterResultRef === resultRef;
    });
    Ui.runBackground('載入高風險條件矩陣', function () {
      return global.JetApi.queryTagMatrixScenarios({}).then(function (data) {
        if (!acceptResponse()) { return; }
        viewState.matrixData = data.scenarios || [];
        renderMatrixContent(body, viewState.matrixData);
      }).catch(function (error) {
        // 舊案件／舊 revision 的失敗安靜作廢；當前失敗保持未載入狀態，讓使用者可重新展開重試。
        if (!acceptResponse()) { return; }
        scenariosBox.textContent = '載入失敗；請收合後再展開以重試。';
        throw error;
      });
    });
  }

  // 傳票矩陣(step4):固定欄(傳票號/總帳日/編製者/傳票總額)+ 動態 C 欄;首屏即接第一頁,載入更多續接。
  // 矩陣為全新表(無預覽列混排),故首擊不需清預覽(bindLoadMore 的 clearTarget 省略)。
  function renderVoucherMatrix(box, columns) {
    box.innerHTML =
      '<h4 class="rule-card__title">傳票矩陣（傳票層：每張命中傳票符合哪些條件）</h4>' +
      Ui.pageSearchHtml('依傳票號碼查看') +
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr>' + Ui.sortableHeadCellsHtml('query.tagMatrixVoucherPage', [
            { key: 'documentNumber', label: '傳票號碼' }, { key: 'postDate', label: '總帳日期' },
            { key: 'createdBy', label: '編製人員' }, { key: 'voucherTotal', label: '傳票總額' }]) +
            tagColumnHeadHtml(columns) + '</tr></thead>' +
          '<tbody></tbody>' +
        '</table>' +
      '</div>' +
      '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more"' +
        ' data-action="matrix-vouchers-more">載入更多</button>';

    var tbody = box.querySelector('.preview-table tbody');
    var fixedCols = [
      function (r) { return r.documentNumber; },
      function (r) { return r.postDate; },
      function (r) { return r.createdBy || ''; },
      { cell: function (r) { return Ui.money(r.voucherTotal); },
        className: 'preview-table__amount' }
    ];
    var allCols = fixedCols.concat(tagCellColumns(columns));

    Ui.bindPagedTable(box, {
      autoLoad: true,
      background: true,
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryTagMatrixVoucherPage({ cursor: cursor, pageSize: 200, sort: sort || null, search: search || null });
      },
      appendRows: function (rows) { Ui.appendRowsToTbody(tbody, rows, allCols); },
      clearRows: function () { tbody.innerHTML = ''; },
      loadMore: box.querySelector('[data-action="matrix-vouchers-more"]'),
      table: box.querySelector('.preview-table'),
      search: box.querySelector('[data-page-search]')
    });
  }

  // 行層明細(step4-1):命中傳票之所有行(含未命中行);無 per-voucher 過濾(Task 4 設計)——
  // 整體命中傳票行明細表 + 傳票號欄供對照 + 載入更多。固定欄 + 動態逐行 C tag。
  function renderRowMatrix(box, columns) {
    box.innerHTML =
      '<h4 class="rule-card__title">分錄明細（分錄層：命中傳票的所有分錄，逐列標記）</h4>' +
      Ui.pageSearchHtml('依傳票號碼查看') +
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr>' + Ui.sortableHeadCellsHtml('query.tagMatrixRowPage', [
            { key: 'documentNumber', label: '傳票號碼' }, { key: 'lineItem', label: '項次' }, { key: 'postDate', label: '總帳日期' },
            { key: 'accountCode', label: '科目' }, { key: 'amount', label: '金額' }, { key: 'description', label: '摘要' }]) +
            tagColumnHeadHtml(columns) + '</tr></thead>' +
          '<tbody></tbody>' +
        '</table>' +
      '</div>' +
      '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more"' +
        ' data-action="matrix-rows-more">載入更多</button>';

    var tbody = box.querySelector('.preview-table tbody');
    var fixedCols = [
      function (r) { return r.documentNumber; },
      function (r) { return r.lineItem || '—'; },
      function (r) { return r.postDate; },
      function (r) { return (r.accountCode || '') + ' ' + (r.accountName || ''); },
      { cell: function (r) { return Ui.money(r.amount); },
        className: 'preview-table__amount' },
      function (r) { return r.description || ''; }
    ];
    var allCols = fixedCols.concat(tagCellColumns(columns));

    Ui.bindPagedTable(box, {
      autoLoad: true,
      background: true,
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryTagMatrixRowPage({ cursor: cursor, pageSize: 200, sort: sort || null, search: search || null });
      },
      appendRows: function (rows) { Ui.appendRowsToTbody(tbody, rows, allCols); },
      clearRows: function () { tbody.innerHTML = ''; },
      loadMore: box.querySelector('[data-action="matrix-rows-more"]'),
      table: box.querySelector('.preview-table'),
      search: box.querySelector('[data-page-search]')
    });
  }

  Ui.registerStep('filter', render);
})(window);
