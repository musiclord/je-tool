/*
  Step 4：進階條件篩選（條件 AST + Query Builder）。
  前端只組裝 AST 與渲染；條件由後端轉參數化 SQL 評估，前端不計算規則。

  版面（由上而下）：
    1. 「KCT條件」選取區塊  —— A–J 可複選 toggle，累積成同一情境的條件。
    2. 「自訂篩選條件」選取區塊 —— 四組等寬卡片，點一張＝新增一條可重複的自訂條件。
    3. 「建立篩選情境」彙總調整區塊 —— 彙總全部已選條件，設定數值/下拉/AND-OR；
       KCT 名稱與動機可沿用自動值或留白，一般自訂情境仍必填。
    4. 預覽結果／已儲存情境／高風險條件矩陣（行為不變）。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

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
       1. 專案切換（projectCode 變）→ 全重置。
       2. 已存清單被替換（savedScenarios 參照變＝commit 成功、移除情境、resume、resetWorkflow）
          → 重置詳情與矩陣——index 對位與命中都可能已變。
       3. 已保存 resultRef 換版（revision／scope 變）→ 只作廢資料快取，保留展開狀態。
       4. Filter 依賴的底層資料世代變（GL／科目配對／授權清單／行事曆）→ 只作廢資料快取：
          已存定義沒變、展開狀態保留，但命中是舊資料算的，restore 會就地重抓。TB 不在此集合。
     ============================================================================ */
  function freshViewState() {
    return {
      customPickerOpen: false,
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
  var OUTPUT_ANCHOR_LABEL = '輸出錨點（第 1 條）';
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
    return hasKctMarker(scenario) || scenario.source === 'kct' ? 'kct' : null;
  }

  function requiresScenarioMetadata(draft) {
    return !hasKctMarker(draft);
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
    var source = scenarioSource(s);
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
    if (carrier !== 'none' && rdeFieldValueType(clean.fieldId) === 'money') {
      wire.amountBasis = clean.amountBasis;
    }
    return wire;
  }

  // 群組組合器：群組內規則 join 的一致值——任一規則為 OR 即「任一(OR)」，否則「全部(AND)」。空群組
  // 回退「全部」。組合器＝把群組內各規則 join 設為同值，後端逐條 join 評估等價（全 AND＝符合全部、全 OR＝任一）。
  function groupCombinator(group) {
    return group.rules.some(function (r) { return r.join === 'OR'; }) ? 'OR' : 'AND';
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
    return (editable[1] && editable[1].join === 'AND') ? 'AND' : 'OR';
  }

  // 草稿送出點專用投影：先把所有可編輯組的 join 收斂成有效組間運算子（單一情境層運算子模型；預設 I
  // 組不動——固定 AND、恆排最後），再走 toWireScenario。這保證任何未預見的操作路徑下「顯示＝送出」
  // 都成立；收斂直接寫回草稿，讓狀態與段控恆一致。為何不放進 toWireScenario：已存情境重投影
  //（savedScenarios.map(toWireScenario)、惰性預覽、移除情境）時 __kctPresetGroup 已被剝除，無從分辨
  // I 組，在那裡 normalize 會把 I 的固定 AND 誤改成組間運算子（OR 情境下語意直接錯掉）。已存情境在
  // 保存當下已經過本收斂，重投影維持原樣即正確。
  function toWireDraft(draft) {
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

      return '<button type="button" class="' + cls + '"' +
          ' data-kct-letter="' + item.letter + '"' +
          ' aria-pressed="' + pressed + '"' +
          (disabled ? ' disabled aria-disabled="true"' : '') + '>' +
        '<span class="picker-card__letter" aria-hidden="true">' + Ui.esc(item.letter) + '</span>' +
        '<span class="picker-card__label" title="' + Ui.esc(item.label) + '">' + Ui.esc(item.label) + '</span>' +
        (note ? '<span class="picker-card__note">' + Ui.esc(note) + '</span>' : '') +
        (state === 'elsewhere' ? '<span class="picker-card__elsewhere">也在其他組</span>' : '') +
        (state === 'preset' ? '<span class="picker-card__preset-mark">情境層級</span>' : '') +
      '</button>';
    }).join('');

    return (
      '<section class="condition-picker condition-picker--kct">' +
        '<div class="condition-picker__head">' +
          '<h3 class="condition-picker__title">KCT條件</h3>' +
          '<span class="condition-picker__count">作用中組已選 ' + selectedCount + ' 項</span>' +
        '</div>' +
        '<p class="condition-picker__intro">點選方法學檢核清單（A–J）。條件會加到下方「作用中」的組；藍色亮起＝該訊號在作用中組，「也在其他組」＝別組也用了。非營業日(I) 為情境層級（黃標），套用到整個情境。</p>' +
        '<div class="condition-picker__grid condition-picker__grid--kct">' + cells + '</div>' +
      '</section>'
    );
  }

  /* ============================================================================
     區塊 2：自訂篩選條件 選取（四組等寬卡片，點一張＝新增一條可重複的條件）
     等寬/等高靠 CSS grid（condition-picker__grid）統一欄寬；每張卡固定內部結構（mark「＋」加入示意
     ＋label）。未符合科目配對內容資格的卡片仍顯示，但停用並註明缺件；kct 分組不在此渲染（KCT 有自己的區塊）。
     自訂條件可重複新增，故是「加入」非 toggle，不顯示任何計數（已移除舊版 ×N 徽章）。
     ============================================================================ */
  function customPickerHtml() {
    var imp = Store.getState().importState;
    var customTypes = Ui.FILTER_RULE_TYPES.filter(function (t) {
      return !t.requiresAuthorizedPreparers || !!imp.authorizedPreparer;
    });

    var groupsHtml = Ui.FILTER_RULE_GROUPS.map(function (grp) {
      if (grp.key === 'kct') { return ''; }
      var cards = customTypes.filter(function (t) { return t.group === grp.key; }).map(function (t) {
        var mappingNote = accountMappingRequirementNote(t.value);
        var disabled = !!mappingNote;
        var cls = 'picker-card picker-card--custom' + (disabled ? ' picker-card--disabled' : '');
        return '<button type="button" class="' + cls + '"' +
            ' data-rule-type="' + t.value + '"' +
            (disabled ? ' disabled aria-disabled="true"' : ' data-action="add-rule"') + '>' +
          '<span class="picker-card__mark" aria-hidden="true">＋</span>' +
          '<span class="picker-card__label" title="' + Ui.esc(t.quickLabel || t.label) + '">' + Ui.esc(t.quickLabel || t.label) + '</span>' +
          (mappingNote ? '<span class="picker-card__note">' + Ui.esc(mappingNote) + '</span>' : '') +
        '</button>';
      }).join('');
      if (!cards) { return ''; }
      return '<div class="condition-picker__group">' +
        '<span class="condition-picker__group-label">' + Ui.esc(grp.label) + '</span>' +
        '<div class="condition-picker__grid condition-picker__grid--custom">' + cards + '</div>' +
      '</div>';
    }).join('');

    // 展開狀態取自 viewState：加條件會 bump 全重繪，挑選區不能因此收回（連點多張卡的操作連續性）。
    var open = viewState.customPickerOpen;
    return (
      '<section class="condition-picker condition-picker--custom">' +
        '<button type="button" class="condition-picker__toggle" data-action="toggle-custom-picker" aria-expanded="' +
          (open ? 'true' : 'false') + '">' +
          '<span class="condition-picker__toggle-caret" aria-hidden="true">' + (open ? '▾' : '▸') + '</span>' +
          '<span class="condition-picker__title">自訂篩選條件</span>' +
          '<span class="condition-picker__toggle-hint">展開以加入金額、日期、科目配對等自訂條件</span>' +
        '</button>' +
        '<div class="condition-picker__body" data-bind="custom-picker-body"' + (open ? '' : ' hidden') + '>' +
          '<p class="condition-picker__intro">點卡片新增條件（可重複），會加到下方「作用中」的組。</p>' +
          groupsHtml +
        '</div>' +
      '</section>'
    );
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

  // 預設(I) 情境層級區塊：唯讀白話＋「情境層級」標籤＋移除；明示它套用到整個情境（與上方條件 AND），不屬於
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
        '<p class="scenario-preset__note">套用到整個情境（與上方條件 AND）</p>' +
      '</div>'
    );
  }

  // 組合器段控（segmented control）：兩格 AND/OR、mono、tooltip 給白話、目前值高亮。底層仍是互斥 radio，
  // 沿用既有 data-set-combinator/data-set-join 綁定（值仍為 AND/OR）。modifier 區分組內（中性）與組間
  // （藍色、情境層）。extraAttrs 帶該段控的綁定屬性（data-set-combinator/data-gi 或 data-set-join）。
  function comboSegment(name, current, extraAttrs, modifier, disabledValue) {
    function seg(val, tip) {
      var on = val === current;
      var disabled = val === disabledValue;
      return (
        '<label class="combo-seg__opt' + (on ? ' is-on' : '') + (disabled ? ' is-disabled' : '') +
          '" title="' + Ui.esc(tip) + '">' +
          '<input type="radio" name="' + name + '" value="' + val + '"' +
            (on ? ' checked' : '') + (disabled ? ' disabled aria-disabled="true"' : '') +
            (extraAttrs ? ' ' + extraAttrs : '') + '>' +
          '<span class="combo-seg__txt">' + val + '</span>' +
        '</label>'
      );
    }
    return (
      '<span class="combo-seg ' + modifier + '">' +
        seg('AND', '每個條件都要成立') +
        seg('OR', '符合任一個就好') +
      '</span>'
    );
  }

  // 群組的比對範圍是 AST 資料，不是前端運算模式。fieldset/legend 把兩個 radio 組成一個可報讀的
  // 控制項；常駐說明明示 sameVoucher 的輸出錨點、跨列佐證與 AND 限制。
  function matchScopeHtml(group, gi) {
    var current = groupMatchScope(group);
    var helpId = 'match-scope-help-' + gi;
    var options = Ui.FILTER_MATCH_SCOPE_OPTIONS.map(function (option) {
      var on = option.value === current;
      return (
        '<label class="combo-seg__opt' + (on ? ' is-on' : '') + '">' +
          '<input type="radio" name="match-scope-' + gi + '" value="' + Ui.esc(option.value) + '"' +
            ' data-group-bind="matchScope" data-gi="' + gi + '"' + (on ? ' checked' : '') + '>' +
          '<span class="combo-seg__txt">' + Ui.esc(option.label) + '</span>' +
        '</label>'
      );
    }).join('');
    var help = current === 'sameVoucher'
      ? '第 1 條標示為「' + OUTPUT_ANCHOR_LABEL + '」；' + SAME_VOUCHER_EXPLANATION +
        '。至少需要 2 條條件，條件之間固定為 AND。'
      : '所有條件都必須由同一分錄列符合。';

    return (
      '<fieldset class="match-scope" aria-describedby="' + helpId + '">' +
        '<legend class="match-scope__legend">比對範圍</legend>' +
        '<span class="combo-seg combo-seg--group">' + options + '</span>' +
        '<p class="match-scope__help" id="' + helpId + '">' + help + '</p>' +
      '</fieldset>'
    );
  }

  // 一塊 well（一個可編輯條件組）：組合器段控（該組 ≥2 條件才顯示）＋條件清單。multi（≥2 組）時段控放進
  // 組標頭「第 N 組」旁（組內中性段控，與組間藍色段控分層）；single 時段控放 well 頂端、前綴白話 lead
  // 「條件之間」。預設(I) 不再併入 well（改為情境層級獨立區塊，見 presetBlockHtml）。
  function setWellHtml(group, gi, setNumber, multi, isActive) {
    var condCount = group ? group.rules.length : 0;
    var sameVoucher = groupMatchScope(group) === 'sameVoucher';
    var comb = sameVoucher ? 'AND' : (group && group.rules.length ? groupCombinator(group) : 'AND');
    var childAttrs = 'data-set-combinator data-gi="' + gi + '"';
    var segment = (group && condCount >= 2)
      ? comboSegment('set-combinator-' + gi, comb, childAttrs, 'combo-seg--group', sameVoucher ? 'OR' : null)
      : '';

    var rows = group ? group.rules.map(function (rule, ri) { return ruleRowHtml(rule, group, gi, ri); }).join('') : '';
    var scope = group ? matchScopeHtml(group, gi) : '';

    // 作用中徽章／非作用中提示（僅 multi、可編輯組）：標示上方面板新增條件的落點。非作用中組的提示
    // 帶「仍參與篩選」——淡化＋鎖定是「停用」慣例，必須明說鎖定只代表「不是新增條件的落點」，
    // 該組條件照樣進後端 SQL（所有非空組都參與篩選）。
    var marker = (multi && group)
      ? (isActive
          ? '<span class="scenario-set__badge">作用中</span>'
          : '<span class="scenario-set__hint">仍參與篩選・點此設為作用中</span>')
      : '';

    var head = '';
    var chooser = '';
    if (multi) {
      head = '<div class="scenario-set__head">' +
          '<span class="scenario-set__title">第 ' + setNumber + ' 組</span>' + marker + segment +
          (group ? '<button type="button" class="btn btn--ghost scenario-set__remove" data-action="remove-set" data-gi="' + gi + '">移除這組</button>' : '') +
        '</div>';
    } else if (segment) {
      chooser = '<div class="combo-row">' +
          '<span class="combo-row__lead">條件之間</span>' + segment +
        '</div>';
    }

    var cls = 'scenario-flat' + (multi ? ' scenario-set' : '') +
      (multi && isActive ? ' scenario-set--active' : '') +
      (multi && group && !isActive ? ' scenario-set--locked' : '');
    // multi 可編輯組整塊 well 可點＝設作用中（見 bind 的 data-active-target）。鎖定組後代 pointer-events
    // 全關，hover 落在 well 上，title 由此浮出、重申參與語意（不佔版面、不加視覺噪音）。
    var activeAttr = (multi && group) ? ' data-active-target data-gi="' + gi + '"' : '';
    if (multi && group && !isActive) {
      activeAttr += ' title="非作用中的組仍會參與篩選；點擊設為作用中，上方新增的條件才會加到這組"';
    }
    var emptyMsg = (multi && group && !isActive)
      ? '先點這組設為作用中，再從上方挑條件加入。'
      : '從上面挑「KCT條件」或「自訂篩選條件」，會加到這一組。';

    return (
      '<div class="' + cls + '"' + activeAttr + '>' +
        head + scope + chooser +
        '<div class="scenario-flat__list">' +
          (rows || '<p class="empty-state">' + emptyMsg + '</p>') +
        '</div>' +
      '</div>'
    );
  }

  // 條件組之間的連接器：做成一條水平軌道（spine），藍色段控置中跨在線上——父／情境層運算子，與組內中性
  // 段控明顯分層。data-set-join 設「所有可編輯群組」的 join 為一致值（單一組間運算子）。name 以 gi 唯一，
  // 避免 3 組以上時多個連接器共用 name 互相撞群（各連接器顯示同一致值即可）。
  function interSetConnectorHtml(joinValue, gi) {
    return (
      '<div class="set-rail">' +
        '<span class="set-rail__lead">組間</span>' +
        comboSegment('set-join-' + gi, joinValue, 'data-set-join', 'combo-seg--scenario') +
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

    // 只有預設群組（如只選了 I）：只呈現情境層級區塊。
    if (editable.length === 0) {
      return presetBlocks;
    }

    var multi = editable.length >= 2;
    var active = activeEditableGroup(draft);
    var interJoin = scenarioJoin(draft); // 與 read-back、新組繼承、wire normalize 同一推導點
    var wells = editable.map(function (e, idx) {
      var isFirst = idx === 0;
      var connector = isFirst ? '' : interSetConnectorHtml(interJoin, e.gi);
      return connector + setWellHtml(e.group, e.gi, idx + 1, multi, e.group === active);
    }).join('');
    return wells + presetBlocks;
  }

  // 行內布林：把一組條件文字以指定運算子（AND/OR）相連，運算子上色（opClass）。條件逐一 Ui.esc，
  // 運算子為字面 AND/OR（安全）。
  function exprJoin(labels, op, opClass) {
    var opHtml = ' <span class="expr-op ' + opClass + '">' + op + '</span> ';
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

  function groupReadBackExpressionHtml(group) {
    var labels = group ? group.rules.map(function (r) { return ruleSummaryLabel(r, 0); }) : [];
    if (groupMatchScope(group) === 'sameVoucher') {
      if (labels.length === 0) { return ''; }
      var sameVoucherExpression = Ui.esc(OUTPUT_ANCHOR_LABEL + '：' + labels[0]);
      if (labels.length > 1) {
        var sameVoucherOp = ' <span class="expr-op expr-op--group">AND</span> ';
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
      var opHtml = ' <span class="expr-op expr-op--group">' + op + '</span> ';
      expression = '（' + expression + opHtml + Ui.esc(labels[i]) + '）';
    }
    return expression;
  }

  // 整句回顯（read-back）：句首白話 lead＋行內布林式，把 AND/OR 寫進去（鏡像控制項：OR＝情境層藍粗、
  // AND＝組內灰）。同時可直接作為底稿的條件邏輯。條件文字用 ruleSummaryLabel（index 0 去前綴）＋
  // presetAtomLabel。
  function readBackHtml(draft) {
    var editable = [];
    var presets = [];
    draft.groups.forEach(function (g) {
      if (g.__kctPresetGroup) { presets.push(g); } else { editable.push(g); }
    });

    // 只看「有條件」的可編輯組：剛按〔＋另一組條件〕還沒填的空組不進 read-back，避免懸空運算子（如 … OR AND …）。
    var ne = editable.filter(function (g) { return g.rules.length > 0; });

    var exprHtml = '';
    if (ne.length === 1) {
      exprHtml = groupReadBackExpressionHtml(ne[0]);
    } else if (ne.length >= 2) {
      var sop = scenarioJoin(draft); // 不讀非空組陣列 ne[1]——空組被濾除時會與段控讀到不同組而顯示錯位
      var parts = ne.map(function (g) {
        var inner = groupReadBackExpressionHtml(g);
        return g.rules.length > 1 && !hasMixedEffectiveRuleJoins(g) ? '（' + inner + '）' : inner;
      });
      exprHtml = parts.join(' <span class="expr-op expr-op--scenario">' + sop + '</span> ');
    }

    // 預設(I)：情境層級、AND 到整個情境（Option A）。附在最後；可編輯式在接 AND 預設段之前包一層
    // 括號消歧——多組本就要包；單一組含 ≥2 條時也要包：「a OR b AND 非營業日」慣例讀作
    // a OR (b AND I)，實際語意是 (a OR b) AND I。不論組內 AND/OR 一律包（AND 時括號無害）。
    if (presets.length) {
      var andOp = ' <span class="expr-op expr-op--scenario">AND</span> ';
      var presetExpr = presets.map(function (p) { return Ui.esc(presetAtomLabel(p)); }).join(andOp);
      var needsParens = ne.length >= 2 || (ne.length === 1 && ne[0].rules.length >= 2);
      exprHtml = exprHtml
        ? (needsParens ? '（' + exprHtml + '）' : exprHtml) + andOp + presetExpr
        : presetExpr;
    }

    if (!exprHtml) { return ''; }

    return (
      '<p class="scenario-readback">' +
        '<span class="scenario-readback__lead">這個情境會找出符合下列邏輯的分錄：</span>' +
        '<span class="scenario-readback__expr">' + exprHtml + '</span>' +
      '</p>'
    );
  }

  // 教學空狀態：無條件時指向上方 palette（NN/g 空狀態三職責：狀態＋學習線索＋指向入口）。
  function teachingEmptyStateHtml() {
    return (
      '<div class="scenario-empty">' +
        '<p class="scenario-empty__title">還沒有任何條件</p>' +
        '<p class="scenario-empty__hint">到上面挑一張「KCT條件」卡，或展開「自訂篩選條件」；挑到的條件會落到這裡讓你設定數值。</p>' +
      '</div>'
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
      statusText += ' 已載入的情境來自舊版或缺少目前版本參照；請重新保存後再載入矩陣、完整命中或報告。';
    }

    return (
      '<section class="population-scope" aria-labelledby="population-scope-heading">' +
        '<div class="population-scope__head">' +
          '<div>' +
            '<h3 class="population-scope__title" id="population-scope-heading">測試母體</h3>' +
            '<p class="population-scope__hint">整批情境一律只測案件的查核期間。</p>' +
          '</div>' +
        '</div>' +
        '<p class="population-scope__definition">只納入總帳日期落在案件期間內的分錄；期外與無日期列排除。</p>' +
        '<div class="' + statusClass + '">' +
          '<span>' + Ui.esc(statusText) + '</span>' +
          (needsResave
            ? '<button type="button" class="btn btn--ghost btn--tiny" data-action="resave-scenarios">' +
                '以查核期間重新保存</button>'
            : '') +
        '</div>' +
      '</section>'
    );
  }

  function scenarioBuilderHtml(draft) {
    var totalRules = draft.groups.reduce(function (n, g) { return n + g.rules.length; }, 0);
    var body = totalRules === 0 ? teachingEmptyStateHtml() : setsHtml(draft);
    var readback = totalRules === 0 ? '' : readBackHtml(draft);
    var addSet = totalRules === 0 ? '' :
      '<button type="button" class="btn btn--ghost" data-action="add-set">＋ 另一組條件</button>';

    // ≥2 可編輯組時，全域說明作用中模型（上方面板新增的條件會進「作用中」那組；點組可切換）。
    var editableCount = draft.groups.filter(function (g) { return !g.__kctPresetGroup; }).length;
    var activeHint = (editableCount >= 2)
      ? '<p class="scenario-active-hint">點任一組設為「作用中」，上方面板（KCT條件／自訂篩選條件）新增的條件就會加到該組。</p>'
      : '';
    var metadataRequired = requiresScenarioMetadata(draft);
    var metadataMark = metadataRequired
      ? '<em class="form__req">*</em>'
      : '<span class="form__optional">（KCT 條件可選填）</span>';

    return (
      '<section class="rule-card scenario-builder">' +
        '<h3 class="rule-card__title">建立篩選情境</h3>' +
        '<p class="scenario-builder__flow">挑訊號 → 設定數值 → 組合 → 命名保存</p>' +
        '<label class="form__row">' +
          '<span class="form__label">情境名稱 ' + metadataMark + '</span>' +
          '<input class="form__input" type="text" data-bind="scenario-name" placeholder="例：摘要異常且金額偏高" value="' +
            Ui.esc(draft.name) + '">' +
        '</label>' +
        '<label class="form__row">' +
          '<span class="form__label">篩選動機說明 ' + metadataMark + '</span>' +
          '<textarea class="form__input" rows="4" data-bind="scenario-rationale" placeholder="說明這個情境為何值得保留到工作底稿">' +
            Ui.esc(draft.rationale) + '</textarea>' +
        '</label>' +
        activeHint +
        body +
        readback +
        '<p class="form-notice" data-bind="scenario-notice" role="alert" hidden></p>' +
        '<div class="panel__actions">' +
          addSet +
          '<button type="button" class="btn btn--ghost" data-action="preview-population"' +
            ' title="開啟 GL 標準化資料預覽；這是設定條件的參考，不代表目前所選母體的命中集合">預覽標準化 GL</button>' +
          '<button type="button" class="btn btn--ghost" data-action="preview-scenario">預覽這個情境</button>' +
          '<button type="button" class="btn" data-action="save-scenario">保存為篩選情境</button>' +
        '</div>' +
      '</section>'
    );
  }

  function ruleRowHtml(rule, group, gi, ri) {
    // 條件列不再有逐條 AND/OR——群組內的結合改由群組層級的「組合器」統一決定（見 scenarioBuilderHtml）。
    // 型別下拉以同一套四組（optgroup）呈現，與自訂分組一致，強化分類語彙。
    var available = availableRuleTypes();
    var typeOptions = Ui.FILTER_RULE_GROUPS.map(function (grp) {
      var opts = available.filter(function (t) { return t.group === grp.key; }).map(function (t) {
        return '<option value="' + t.value + '"' + (rule.type === t.value ? ' selected' : '') + '>' +
          Ui.esc(t.label) + '</option>';
      }).join('');
      return opts ? '<optgroup label="' + Ui.esc(grp.label) + '">' + opts + '</optgroup>' : '';
    }).join('');

    var sameVoucher = groupMatchScope(group) === 'sameVoucher';
    var scopeLabel = sameVoucher
      ? (ri === 0
          ? '<span class="rule-row__field-label">' + Ui.esc(OUTPUT_ANCHOR_LABEL) + '</span>'
          : '<span class="rule-row__field-label">同傳票佐證（第 ' + (ri + 1) + ' 條）</span>')
      : '';
    var describedBy = sameVoucher ? ' aria-describedby="match-scope-help-' + gi + '"' : '';

    return (
      '<div class="rule-row" data-gi="' + gi + '" data-ri="' + ri + '"' + describedBy + '>' +
        scopeLabel +
        '<select class="rule-row__type" data-rule-bind="type">' + typeOptions + '</select>' +
        '<div class="rule-row__controls">' + ruleControlsHtml(rule, gi, ri) + '</div>' +
        '<button type="button" class="btn btn--ghost" data-action="remove-rule">移除</button>' +
      '</div>'
    );
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
      var boxes = Ui.taxonomyCategories(state).map(function (category, index) {
        var id = 'cat-' + gi + '-' + ri + '-' + idsKey + '-' + index;
        return '<label class="category-option" for="' + id + '">' +
          '<input type="checkbox" id="' + id + '" data-category-bind="' + idsKey + '"' +
            ' value="' + Ui.esc(category.categoryId) + '"' +
            (selected.indexOf(category.categoryId) >= 0 ? ' checked' : '') + '>' +
          '<span>' + Ui.esc(category.label) + '</span>' +
        '</label>';
      }).join('');
      return '<fieldset class="category-select">' +
        '<legend class="category-select__legend">' + Ui.esc(legend) + '</legend>' +
        boxes +
      '</fieldset>';
    }

    switch (rule.type) {
      case 'prescreen':
        return '<select data-rule-bind="prescreenKey">' + availablePrescreenKeys().map(function (o) {
          return '<option value="' + o.value + '"' + (rule.prescreenKey === o.value ? ' selected' : '') + '>' +
            o.label + '</option>';
        }).join('') + '</select>';

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
        var accountPairHtml = '<select data-rule-bind="pairMode">' + Ui.ACCOUNT_PAIR_MODE_OPTIONS.map(function (o) {
            return '<option value="' + o.value + '"' + (rule.pairMode === o.value ? ' selected' : '') + '>' +
              o.label + '</option>';
          }).join('') + '</select>';
        if (rule.pairMode !== 'creditAnchor') {
          accountPairHtml += categoryMultiSelect('debitCategoryIds', 'debitCategory',
            rule.pairMode === 'debitAnchor' ? '借方錨定類別' : '借方類別');
        }
        if (rule.pairMode !== 'debitAnchor') {
          accountPairHtml += categoryMultiSelect('creditCategoryIds', 'creditCategory',
            rule.pairMode === 'creditAnchor' ? '貸方錨定類別' : '貸方類別');
        }
        return accountPairHtml;

      case 'specialAccountCategoryPair':
        // accountPair 的姊妹條件：三模式皆需借方類別(A) 與貸方類別(B) 皆填（否定模式同樣需要
        // 兩類別才能判定「不存在」），故一律呈現兩組多選，重用同一 helper。
        return '<select data-rule-bind="pairMode">' + Ui.SPECIAL_PAIR_MODE_OPTIONS.map(function (o) {
            return '<option value="' + o.value + '"' + (rule.pairMode === o.value ? ' selected' : '') + '>' +
              o.label + '</option>';
          }).join('') + '</select>' +
          categoryMultiSelect('debitCategoryIds', 'debitCategory', '借方類別(A)') +
          categoryMultiSelect('creditCategoryIds', 'creditCategory', '貸方類別(B)');

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
        return '<span class="rule-row__field-label" title="金額末幾位連續為 0；位數越多越接近整數">尾數連續 0 位數</span>' +
          '<input type="number" data-rule-bind="digits" min="1" max="12" step="1" value="' +
            Ui.esc(rule.digits) + '">';

      case 'customPreparerEntryCount':
        return '<span class="rule-row__field-label">所選母體內編製人員張數 ≤</span>' +
          '<input type="number" data-rule-bind="maxEntries" min="1" step="1" value="' +
            Ui.esc(rule.maxEntries) + '">';

      case 'customAccountEntryCount':
        return '<span class="rule-row__field-label">所選母體內科目張數 ≤</span>' +
          '<input type="number" data-rule-bind="maxEntries" min="1" step="1" value="' +
            Ui.esc(rule.maxEntries) + '">';

      case 'revenueDebitNearQuarterEnd':
        return '<span class="rule-row__field-label" title="總帳(過帳)日落在曆年季底前 N 天，且科目為收入、在借方側">季末前</span>' +
          '<input type="number" data-rule-bind="windowDays" min="1" max="92" step="1" placeholder="天數" value="' +
            Ui.esc(rule.windowDays) + '">' +
          '<span class="rule-row__sep">天・借記收入</span>';

      case 'revenueWithoutNormalCounterpart':
        return '<span class="rule-row__field-label" title="貸方為收入，但同傳票無應收/預收的借方分錄">貸收入・借方非應收/預收</span>';

      case 'manualRevenueEntry':
        return '<span class="rule-row__field-label" title="科目為收入且為人工分錄">收入・人工分錄</span>';

      case 'trailingDigits':
        // 範例提示改常駐 helper text（NN/g、GOV.UK：hover-only/title 對鍵盤/觸控/報讀器不友善；
        // 欄位有預設值 000000，placeholder 不可用，故範例放欄位下方常駐一行）。
        return '<span class="rule-row__field-label" title="顯示金額整數尾數符合任一樣態（捨小數）">金額尾數為</span>' +
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

  function ruleSummaryLabel(rule, index) {
    var prefix = index === 0 ? '' : '(' + (rule.join === 'OR' ? 'OR' : 'AND') + ') ';
    switch (rule.type) {
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
            : '借方 ' + pairDebit + '・貸方 ' + pairCredit);
        return prefix + '科目配對分析：' + (pairMode ? pairMode.label : rule.pairMode) + '（' + pairDetail + '）';
      }
      case 'specialAccountCategoryPair': {
        var specialMode = Ui.SPECIAL_PAIR_MODE_OPTIONS.filter(function (o) { return o.value === rule.pairMode; })[0];
        var specialState = Store.getState();
        return prefix + '特殊科目配對：借 ' +
          categoryIdsLabel(specialState, ruleCategoryIds(rule, 'debitCategoryIds', 'debitCategory')) +
          '／貸 ' +
          categoryIdsLabel(specialState, ruleCategoryIds(rule, 'creditCategoryIds', 'creditCategory')) +
          '（' + (specialMode ? specialMode.label : rule.pairMode) + '）';
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
        return prefix + '所選母體內編製人員張數 ≤ ' + rule.maxEntries;
      case 'customAccountEntryCount':
        return prefix + '所選母體內科目張數 ≤ ' + rule.maxEntries;
      case 'revenueDebitNearQuarterEnd':
        return prefix + '季末前 ' + (rule.windowDays || '…') + ' 天借記收入';
      case 'revenueWithoutNormalCounterpart':
        return prefix + '貸收入・借方非應收/預收';
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
    return (groups || []).map(function (group, gi) {
      var pills = group.rules.map(function (rule, ri) {
        return '<span class="scenario-pill">' + Ui.esc(ruleSummaryLabel(rule, ri)) + '</span>';
      }).join('');
      var head = gi === 0 ? '' :
        '<span class="scenario-pill scenario-pill--join">' + (group.join === 'OR' ? 'OR' : 'AND') + '</span>';
      return head + '<span class="scenario-pill scenario-pill--group">群組 ' + (gi + 1) + '</span>' + pills;
    }).join('');
  }

  function previewPaneHtml(preview) {
    var body;
    if (!preview) {
      body = '<p class="empty-state">先建立規則，再按「預覽這個情境」。</p>';
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
        '<h3 class="rule-card__title">預覽結果</h3>' +
        '<div data-bind="preview-pane-body"' + (preview ? '' : ' data-empty="1"') + '>' + body + '</div>' +
      '</section>'
    );
  }

  function savedScenariosHtml(state) {
    var saved = state.filter.savedScenarios;
    var committed = committedPopulationScope(state);
    var items = saved.length === 0
      ? '<p class="empty-state">尚未保存任何篩選情境。先預覽，再決定是否保留到工作底稿。</p>'
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
                  '">' + (open ? '收合' : '詳情') + '</button>' +
                '<button type="button" class="btn btn--ghost" data-action="remove-scenario" data-index="' + i +
                  '"' + (confirmingRemoval ? ' hidden' : '') + '>移除</button>' +
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
            ? '<p class="form-notice">這批情境沒有可用的版本參照。請在「測試母體」區重新保存；完成前不載入矩陣、完整命中或報告。</p>'
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
    return (
      '<section class="report-output">' +
        '<div class="report-output__head">' +
          '<div>' +
            '<h3 class="report-output__title">完成條件篩選</h3>' +
            '<p class="report-output__hint">將目前已存情境凍結為 CriteriaSelectionReport；修改情境後必須重新產生。</p>' +
          '</div>' +
          '<button type="button" class="btn" data-action="export-criteria-report"' +
            (canExport ? '' : ' disabled') + '>' +
            (artifact ? '重新產生條件篩選報告' : '完成條件篩選並產生報告') +
          '</button>' +
        '</div>' +
        Ui.reportArtifactListHtml(artifact ? [artifact] : [],
          canExport ? '已存情境尚未產生目前版本報告。' :
            (state.filter.savedScenarios.length > 0 && !committedPopulationScope(state)
              ? '已存情境缺少可用版本；請先回到測試母體區重新保存。'
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
          '<p class="form-notice">目前已存情境缺少可用版本。請先在「測試母體」區重新保存，矩陣才會開放。</p>' +
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
      return '<th title="' + Ui.esc(c.name) + '">' + Ui.esc(c.label) + '</th>';
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

  function render(container, state) {
    syncViewState(state); // 專案切換／已存清單替換時對齊檢視狀態（先於任何讀取 viewState 的 HTML 生成）

    if (!state.project) {
      container.innerHTML = Ui.noProjectPanel('進階條件篩選');
      Ui.bindNoProjectPanel(container);
      return;
    }

    var draft = state.filter.draft;
    var saved = state.filter.savedScenarios;
    var prescreenRun = state.lastRuns.prescreen;
    // 草稿條件數＝畫面上可見的條件塊數：可編輯組逐條計，預設組（I）整塊計 1——底層雖是週末/假日
    // 兩條 rule，建構器只呈現一個情境層級區塊，計數與使用者所見對齊。
    var draftRuleCount = draft.groups.reduce(function (sum, g) {
      return sum + (g.__kctPresetGroup ? 1 : g.rules.length);
    }, 0);

    container.innerHTML =
      '<div class="panel panel--wide">' +
        '<h2 class="panel__title">進階條件篩選</h2>' +
        '<p class="panel__hint">挑選方法學條件或自訂條件，組成篩選情境；最多保存 10 個。</p>' +
        Ui.mappingReviewBannerHtml(state) +
        Ui.staleNoticeHtml(state, 'filter',
          '上游資料已變更，先前的篩選命中已失效；情境定義仍保留，重新保存後即可再取得命中結果。') +
        populationScopeHtml(state) +
        '<div class="stats-bar">' +
          '<div class="stat-card">' +
            '<span class="stat-card__value">' + (prescreenRun ? '已執行' : '未執行') + '</span>' +
            '<span class="stat-card__label">風險預篩選</span>' +
          '</div>' +
          '<div class="stat-card">' +
            '<span class="stat-card__value">' + draftRuleCount + '</span>' +
            '<span class="stat-card__label">草稿條件數</span>' +
          '</div>' +
          '<div class="stat-card">' +
            '<span class="stat-card__value">' + saved.length + ' / 10</span>' +
            '<span class="stat-card__label">已儲存情境</span>' +
          '</div>' +
        '</div>' +
        kctPickerHtml(draft) +
        customPickerHtml() +
        scenarioBuilderHtml(draft) +
        previewPaneHtml(state.filter.preview) +
        savedScenariosHtml(state) +
        tagMatrixHtml(state) +
        criteriaReportHtml(state) +
        Ui.stepFooterHtml(state) +
      '</div>';

    bind(container);
    restoreViewState(container);
  }

  // bump 全重繪後回填「已展開」惰性面板的內容（hidden 與按鈕文字已在 HTML 生成時還原；這裡補內容：
  // 已存情境詳情與矩陣用 viewState 快取回填、無快取才重抓——見 ensureScenarioPreview／ensureMatrixContent）。
  function restoreViewState(container) {
    Object.keys(viewState.openScenarios).forEach(function (k) {
      if (viewState.openScenarios[k]) { ensureScenarioPreview(container, Number(k)); }
    });
    if (viewState.matrixOpen) { ensureMatrixContent(container); }
  }

  // 取消後回原移除鈕；確認後原列已不存在，改回同一視覺位置的下一列、
  // 末列則回前一列，清單歸零時回清單標題，不讓焦點落回 document body。
  function restoreScenarioRemovalFocus(container, index) {
    function target() {
      var buttons = Array.prototype.slice.call(container.querySelectorAll(
        '[data-action="remove-scenario"]:not([hidden])'));
      if (buttons.length > 0) { return buttons[Math.min(index, buttons.length - 1)]; }
      return container.querySelector('[data-bind="saved-scenarios-title"]');
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
    var metadataRequired = requiresScenarioMetadata(draft);
    var noCondition = draft.groups.length === 0 ||
      draft.groups.every(function (g) { return g.rules.length === 0; });
    var oversizedTextSet = hasOversizedTextSet(draft);
    var oversizedTypedSet = hasOversizedTypedSet(draft);
    var incompleteTyped = hasIncompleteTypedRule(draft);

    var problems = [];
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

    var noCondition = draft.groups.length === 0 ||
      draft.groups.every(function (g) { return g.rules.length === 0; });
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
    var el = container.querySelector('.scenario-readback');
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
    body.innerHTML = '<p class="empty-state">條件已變更，請重新預覽。</p>';
    body.setAttribute('data-empty', '1');
  }

  // 已存情境命中預覽的實體渲染＋「載入更多」接線（詳情展開的惰性載入與 bump 重繪後的快取還原共用）。
  // s = filter.preview 回傳的 scenario 物件；scenarioPosition 對齊後端 1-based position（保存順序），
  // 用於 query.filterHitsPage 接續行層明細。
  function renderScenarioHitPreview(previewEl, s, scenarioPosition, allowLoadMore) {
    var rows = s.previewRows.slice(0, 10);
    previewEl.innerHTML =
      '<p class="rule-card__sub">命中 ' + Number(s.count).toLocaleString() + ' 筆／' +
        Number(s.voucherCount).toLocaleString() + ' 張傳票，以下為前 ' + rows.length +
        ' 筆。測試母體：' + Ui.esc(populationScopeLabel(s.populationScope)) + '。</p>' +
      '<div class="preview-table__wrap">' + Ui.previewTableHtml(rows) + '</div>' +
      (allowLoadMore
        ? '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more" data-action="hits-load-more">載入更多</button>'
        : '<p class="form-notice">這是依目前所選母體產生的有界預覽。重新保存情境後，才可載入完整命中。</p>');
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
    Ui.bindLoadMore(moreBtn, function (cursor) {
      return global.JetApi.queryFilterHitsPage({
        scenarioPosition: scenarioPosition, cursor: cursor, pageSize: 200
      }).then(function (page) {
        lastPage = page;
        return page;
      });
    }, function (hitRows) {
      Ui.appendRowsToTbody(tbody, hitRows, cells);
    }, function () {
      // 首擊清掉預覽前 10 列(filter.preview 另一套排序),改接 keyset ASC 全量,避免重複與排序不一致；
      // 同時以系統端欄位定義重建表頭，讓額外欄位與資料列對齊。
      var columns = (lastPage && lastPage.columns) || [];
      cells = Ui.dynamicColumnCells(columns);
      var thead = table ? table.querySelector('thead') : null;
      if (thead) { thead.innerHTML = Ui.dynamicColumnHeadHtml(columns); }
      tbody.innerHTML = '';
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
        // 回應期間已存清單被替換（commit／移除／resume）：index 對位已變，結果作廢，
        // 不寫入剛被 syncViewState 重置的快取（防止舊定義的預覽掛到新清單的同一序位上）。
        if (!acceptResponse()) { return; }
        if (allowLoadMore) { viewState.scenarioPreviews[index] = data.scenario; }
        renderScenarioHitPreview(previewEl, data.scenario, scenarioPosition, allowLoadMore);
      }).catch(function (error) {
        // 舊案件／舊 revision 的失敗不可污染目前案件訊息；當前失敗則留可重試提示並交給共用訊息區。
        if (!acceptResponse()) { return; }
        previewEl.textContent = '載入失敗；請收合後再展開以重試。';
        throw error;
      });
    });
  }

  // 會改變同一列可見控制項組合的規則鍵：改動後必須重繪，否則畫面會停在舊的輸入形狀。
  var STRUCTURAL_RULE_KEYS = ['fieldId', 'operator', 'pairMode'];

  function bind(container) {
    Ui.bindStepFooter(container);

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

    // 自訂條件卡：點一張＝新增一條該型別條件（可重複；併入「作用中」群組 activeEditableGroup；沒有可編輯
    // 組就先開一組、組合器預設「全部(AND)」並設為作用中）。新規則 join 取該組現有組合器以維持群組內一致。
    container.querySelectorAll('[data-action="add-rule"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var target = activeEditableGroup(draft);
        if (!target) {
          target = { join: 'AND', matchScope: 'row', rules: [] };
          draft.groups.push(target);
          setActiveGroup(draft, target);
        }
        var rule = Ui.newFilterRule(btn.getAttribute('data-rule-type'));
        rule.join = groupMatchScope(target) === 'sameVoucher'
          ? 'AND'
          : (target.rules.length ? groupCombinator(target) : 'AND');
        target.rules.push(rule);
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
        Store.setFilterDraft(draft);
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

    // 點條件組的「中性區域」（非表單控制項、非按鈕）＝把該組設為作用中（上方面板新增的落點）。排除
    // button/input/select/textarea/label：避免攔截編輯、避免重繪奪焦；已是作用中或預設組則不動。
    container.querySelectorAll('[data-active-target]').forEach(function (well) {
      well.addEventListener('click', function (e) {
        if (e.target.closest('button, input, select, textarea, label')) { return; }
        var draft = Store.getState().filter.draft;
        var group = draft.groups[Number(well.getAttribute('data-gi'))];
        if (!group || group.__kctPresetGroup || group.__active) { return; }
        setActiveGroup(draft, group);
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
    container.querySelectorAll('[data-group-bind="matchScope"]').forEach(function (control) {
      control.addEventListener('change', function () {
        if (!control.checked) { return; }
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

    // 條件組組合器（段控，每組一個）：設「該可編輯群組(data-gi)」各 rule 的 join＝符合全部/任一。
    // 預設(I) 群組是情境層級、固定 AND（不再隨組合器同步，見 toWireScenario/Option A）。
    container.querySelectorAll('[data-set-combinator]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        if (!radio.checked) { return; }
        var value = radio.value;
        var draft = Store.getState().filter.draft;
        var group = draft.groups[Number(radio.getAttribute('data-gi'))];
        if (!group) { return; }
        if (groupMatchScope(group) === 'sameVoucher' && value === 'OR') { return; }
        group.rules.forEach(function (r) { r.join = value; });
        Store.setFilterDraft(draft);
      });
    });

    // 條件組之間的連接器（整句 radio）：單一組間運算子——把所有「非預設」群組的 join 設為一致值
    //（預設群組的 join 由上面 sync-presets 管，不在此動）。
    container.querySelectorAll('[data-set-join]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        if (!radio.checked) { return; }
        var value = radio.value;
        var draft = Store.getState().filter.draft;
        draft.groups.forEach(function (g) { if (!g.__kctPresetGroup) { g.join = value; } });
        Store.setFilterDraft(draft);
      });
    });

    container.querySelectorAll('.rule-row').forEach(function (row) {
      var gi = Number(row.getAttribute('data-gi'));
      var ri = Number(row.getAttribute('data-ri'));

      row.querySelector('[data-action="remove-rule"]').addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var removed = draft.groups[gi].rules[ri];
        var wasKct = !!(removed && removed[KCT_LETTER_KEY]);
        draft.groups[gi].rules.splice(ri, 1);
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
          Store.patchFilterRule(gi, ri, patch);
          softRefreshReadback(container);
          softExpirePreviewPane(container);
          softRefreshGate(container);
        });
      });

      row.querySelectorAll('[data-rule-bind]').forEach(function (control) {
        var key = control.getAttribute('data-rule-bind');
        if (key === 'type') {
          // 型別切換是結構變動：以 newFilterRule 重建該列、只保留 join（setFilterDraft 整面重繪）。良性
          // 副作用：fresh 不帶 __kctLetter，故此列若原是某 KCT 卡帶入的，改型別後即自然解除身分——picker
          // 會自動取消該卡已選（isKctSelected 找不到帶該字母標記的 rule）。重建即不殘留舊標記。
          control.addEventListener('change', function () {
            var draft = Store.getState().filter.draft;
            var old = draft.groups[gi].rules[ri];
            var wasKct = !!(old && old[KCT_LETTER_KEY]);
            var fresh = Ui.newFilterRule(control.value);
            fresh.join = old.join;
            draft.groups[gi].rules[ri] = fresh;
            if (wasKct) { applyKctNaming(draft); } // 改型別＝解除 KCT 身分，重算命名（與移除一致）
            Store.setFilterDraft(draft);
          });
          return;
        }
        // 值編輯用 input（即時）：patchFilterRule 只 patch、不重建面板（保住焦點），並即時刷新藍色
        // read-back，讓「這個情境會找出…」隨輸入同步更新（修：原本 change 要等下次重繪才更新）。
        // 同時把預覽面板軟更新為失效空狀態——read-back 顯示新值而預覽停留舊命中數是同屏矛盾。
        control.addEventListener('input', function () {
          var patch = {};
          patch[key] = key === 'values' ? control.value.split(/\r?\n/) : control.value;
          Store.patchFilterRule(gi, ri, patch);
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
          if (!acceptResponse() || data.scenario.populationScope !== populationScope) { return; }
          Store.setFilterPreview(data.scenario);
          Store.addMessage('情境預覽：命中 ' + data.scenario.count + ' 筆／' +
            data.scenario.voucherCount + ' 張傳票。', 'info');
        });
      }, { logCompletion: true });
    });

    container.querySelector('[data-action="save-scenario"]').addEventListener('click', function () {
      if (!scenarioGate(container, true)) { return; }
      Ui.run('保存篩選情境', function () {
        var commitState = Store.getState();
        var current = commitState.filter;
        var populationScope = selectedPopulationScope(commitState);

        // 已存情境 + 當前草稿都過同一投影（深拷貝剝除 __kctLetter，並保留 canonical source:'kct'）。
        // 存入 savedScenarios 的也是這份剝乾淨的形狀，故後續惰性預覽其 groups 不含任何 UI-only 標記。
        var scenarios = current.savedScenarios.map(toWireScenario)
          .concat([toWireDraft(current.draft)]);
        if (scenarios.length > 10) {
          Store.addMessage('最多保存 10 個篩選情境；請先移除既有情境。', 'warn');
          return Promise.resolve();
        }
        return global.JetApi.filterCommit({
          populationScope: populationScope,
          scenarios: scenarios
        }).then(function (data) {
          Store.setFilterResultRef(data.resultRef);
          Store.setSavedScenarios(data.scenarios);
          // 保存成功即把草稿重置回初始空狀態：commit 是 replace-all，草稿留著再按一次〔保存〕就會把
          // 同名情境重複帶進 payload 而被後端 invalid_scenario 拒絕。重置走 setFilterDraft（清預覽、
          // 重繪），dirty 旗標與作用中組隨舊 draft 物件一起消失，KCT 卡高亮由空草稿重新推導。必填紅框
          // 不會出現：提示只在使用者按〔預覽〕／〔保存〕當下由 scenarioGate 觸發，重繪即回乾淨表單。
          Store.setFilterDraft({ name: '', rationale: '', groups: [] });
          Store.addMessage('已保存篩選情境（' + data.savedCount + ' / 10）。', 'info');
        });
      }, { logCompletion: true });
    });

    container.querySelectorAll('[data-action="toggle-scenario"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var index = Number(btn.getAttribute('data-index'));
        var body = container.querySelector('[data-bind="scenario-body-' + index + '"]');
        if (!body) { return; }

        body.hidden = !body.hidden;
        btn.textContent = body.hidden ? '詳情' : '收合';
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
  function bindTagMatrix(container) {
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

    scenariosBox.innerHTML = scenarioSummaryTableHtml(columns);
    renderVoucherMatrix(vouchersBox, columns);
    renderRowMatrix(rowsBox, columns);
    body.setAttribute('data-loaded', '1');
  }

  // 確保展開中的矩陣有內容：優先用 viewState 摘要快取（草稿編輯不影響已存情境命中，跨重繪保留安全；
  // savedScenarios 替換時由 syncViewState 重置），沒有快取才發 queryTagMatrixScenarios 並寫入。
  function ensureMatrixContent(container) {
    var body = container.querySelector('[data-bind="matrix-body"]');
    if (!body || body.getAttribute('data-loaded') === '1') { return; }
    if (!committedPopulationScope(Store.getState())) {
      body.innerHTML = '<p class="form-notice">目前沒有可用的已保存版本；請先重新保存情境。</p>';
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
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr><th>傳票號碼</th><th>總帳日期</th><th>編製人員</th>' +
            '<th>傳票總額</th>' + tagColumnHeadHtml(columns) + '</tr></thead>' +
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

    Ui.bindLoadMore(box.querySelector('[data-action="matrix-vouchers-more"]'), function (cursor) {
      return global.JetApi.queryTagMatrixVoucherPage({ cursor: cursor, pageSize: 200 });
    }, function (rows) {
      Ui.appendRowsToTbody(tbody, rows, allCols);
    });
  }

  // 行層明細(step4-1):命中傳票之所有行(含未命中行);無 per-voucher 過濾(Task 4 設計)——
  // 整體命中傳票行明細表 + 傳票號欄供對照 + 載入更多。固定欄 + 動態逐行 C tag。
  function renderRowMatrix(box, columns) {
    box.innerHTML =
      '<h4 class="rule-card__title">分錄明細（分錄層：命中傳票的所有分錄，逐列標記）</h4>' +
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr><th>傳票號碼</th><th>項次</th><th>總帳日期</th><th>科目</th>' +
            '<th>金額</th><th>摘要</th>' + tagColumnHeadHtml(columns) + '</tr></thead>' +
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

    Ui.bindLoadMore(box.querySelector('[data-action="matrix-rows-more"]'), function (cursor) {
      return global.JetApi.queryTagMatrixRowPage({ cursor: cursor, pageSize: 200 });
    }, function (rows) {
      Ui.appendRowsToTbody(tbody, rows, allCols);
    });
  }

  Ui.registerStep('filter', render);
})(window);
