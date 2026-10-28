/*
  第五步：進階條件篩選 —— 條件來源的挑選。
  KCT 卡、「預篩選」分頁、欄位按鈕、常用查核條件與範例，把條件加進作用中的條件組，以及自動產生的情境名稱與動機。
  規則目錄與可不可用都讀後端給的表，前端不另算一套。
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
  var keepMotionKey = forward('keepMotionKey');
  var revealAddedRule = forward('revealAddedRule');
  var requirementNote = forward('requirementNote');
  var ownRequirementNote = forward('ownRequirementNote');
  var announceRemoval = forward('announceRemoval');
  var fillSmoothly = forward('fillSmoothly');
  var groupCombinator = forward('groupCombinator');
  var groupMatchScope = forward('groupMatchScope');
  var readBackExpressionHtml = forward('readBackExpressionHtml');
  var readBackPlainText = forward('readBackPlainText');
  var guardDraftReplacement = forward('guardDraftReplacement');
  var ruleSummaryLabel = forward('ruleSummaryLabel');
  var draftRuleProblems = forward('draftRuleProblems');
  var ruleValueControl = forward('ruleValueControl');
  // 其他檔案定義的常數：只讀，定義它的檔案先載入。
  var EMPTY_FIRST_GROUP = FilterStep.EMPTY_FIRST_GROUP;

  // 樣態選單裡的 KCT 專屬條件；前置條件不足的不列出，已選的條件不能用時原因寫在卡片上（見 ruleSubjectHtml）。
  function isPatternMenuKctType(type) {
    return Ui.FILTER_RULE_TYPES.some(function (t) { return t.value === type && t.group === 'kct' && t.value !== 'trailingDigits'; });
  }

  function availableKctTypes() {
    return Ui.FILTER_RULE_TYPES.filter(function (t) { return isPatternMenuKctType(t.value) && !requirementNote([{ type: t.value }]); });
  }

  // 下拉選單裡停用的欄位寫在選項後面的說明，和欄位按鈕下方那句同一個意思。
  var UNMAPPED_OPTION_SUFFIX = '（' + Ui.GL_FIELD_UNMAPPED + Ui.GL_FIELD_NEXT_STEP + '）';

  // 目前案件已提交的攸關資料元素欄位；沒有時 typed 條件卡停用並說明原因。
  function rdeFieldOptions() {
    return Ui.FilterValues.fields(Store.getState()).filter(function (field) { return field.extra; })
      .map(function (field) { return { id: field.id, fieldId: field.id, valueType: field.type, optionLabel: field.optionLabel }; });
  }

  function rdeFieldValueType(fieldId) {
    var hit = rdeFieldOptions().filter(function (f) { return f.fieldId === fieldId; })[0];
    return hit ? hit.valueType : null;
  }

  function availablePrescreenKeys() {
    return Ui.PRESCREEN_RULES.filter(function (o) {
      return !prescreenRequirementNote(o.value);
    });
  }

  var RETIRED_TYPE_NOTE = '此條件的類型已停用，請刪除此條件';

  // 預篩選條件還缺什麼；鍵已停用時和後端 P8 同一句（使用者 2026-10-14 第三輪勾選頁確認 R3-18）。
  function prescreenRequirementNote(key) {
    var known = Ui.PRESCREEN_RULES.some(function (item) { return item.value === key; });
    return known ? requirementNote([{ type: 'prescreen', prescreenKey: key }]) : RETIRED_TYPE_NOTE;
  }

  /* ============================================================================
     KCT 卡和草稿條件之間的對應，集中在下面少數幾個函式處理，其他程式只呼叫它們。
     一張 KCT 卡 = 一份「rule 規格」陣列（kctRuleSpecs）。加入 = 把這些規格化成真 rule、
     在每條 rule 上打一個穩定的身分標記（__kctLetter = 卡片字母）併進草稿；已選偵測 =
     草稿中是否存在帶該卡字母標記的 rule；移除 = 把所有帶該卡字母標記的 rule 挑掉。

     為何用標記而非結構簽章（取捨顯式）：先前以 type(+field/mode/prescreenKey) 的結構簽章
     猜「這條 rule 是不是這張卡帶進來的」。但 KCT 卡的簽章會與查核員「自訂」的同型別條件
     重疊——例如卡 F(customKeywords) 會被任何手動加入的自訂關鍵字條件誤判為「已選」，且移除
     時只刪「第一條」同簽章 rule，可能誤刪使用者手動加入的條件。改用精確身分標記後，這一整類
     誤判／誤刪的 edge case 直接消失：自訂的同型別條件不帶標記，永遠不會被當成 KCT。

     __kctLetter 是 UI-only 標記（鍵名以雙底線開頭、明示「內部用途、不屬 wire 契約」），絕不可
     送進後端。所有送出點一律經 toWireScenario() 深拷貝並遞迴剝除；有 KCT marker 或已存
     source:'kct' 時另送根層 source:'kct'，groups 內每條 rule 不含任何 UI-only 鍵。
     ============================================================================ */

  // rule 身分標記鍵：標示「這條 rule 是哪張 KCT 卡帶進來的」。UI-only，絕不送 wire（由
  // toWireScenario 剝除）。集中為常數，讓打標記／偵測／剝除三處引用同一事實。
  var KCT_LETTER_KEY = '__kctLetter';

  // FilterConditionRenderer 的同傳票結構片語；read-back textContent 必須逐字鏡像後端。
  var OUTPUT_ANCHOR_LABEL = '主要條件（決定符合條件的分錄）';

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
  //   kind:'preset' → 沿用 FILTER_KCT_PRESETS 的單一 overrides 規格（I 是一條條件括號）。
  function kctRuleSpecs(item) {
    if (item.kind === 'spec') { return [item.overrides]; }
    if (item.kind === 'preset') {
      var preset = Ui.FILTER_KCT_PRESETS.filter(function (p) { return p.key === item.ref; })[0];
      return preset ? [preset.overrides] : [];
    }
    return item.ref ? [{ type: item.ref }] : [];
  }

  // 把一條規格落地成真 rule，並打上該卡的身分標記：以 newFilterRule(type) 為底，套上規格的
  // 覆寫鍵（field/mode/prescreenKey/join/rules…），最後標 __kctLetter = letter（UI-only，剝除後才送 wire）。
  // 規格先整份深拷貝：I 的括號帶子條件陣列，多次加入不得共用同一份陣列或子條件物件。
  function materializeRule(spec, letter) {
    var copy = JSON.parse(JSON.stringify(spec));
    var rule = Ui.newFilterRule(copy.type);
    Object.keys(copy).forEach(function (k) { rule[k] = copy[k]; });
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

  // 卡片「已選」判定（組層級）：看「作用中組」是否含該字母。讓同一訊號可在不同組各自存在，
  // 且在作用中組 toggle 只動該組（避免切換 KCT 時誤刪其他組的同一訊號）。
  function isKctSelectedActive(draft, item) {
    if (kctRuleSpecs(item).length === 0) { return false; }
    var active = activeEditableGroup(draft);
    return !!active && active.rules.some(function (r) { return r[KCT_LETTER_KEY] === item.letter; });
  }

  // KCT 是否「用於其他（非作用中）條件組」——給卡片加淡標記「也在其他組」，維持全局意識。
  function isKctUsedElsewhere(draft, item) {
    var active = activeEditableGroup(draft);
    return draft.groups.some(function (g) {
      return g !== active && g.rules.some(function (r) { return r[KCT_LETTER_KEY] === item.letter; });
    });
  }

  // KCT 卡視覺狀態（給 kctPickerHtml）：
  //   'selected'  = 在作用中組（藍色高亮）。
  //   'elsewhere' = 只在別的（非作用中）組（藍邊「也在其他組」）。
  //   'none'      = 未加入。
  function kctCardState(draft, item) {
    if (isKctSelectedActive(draft, item)) { return 'selected'; }
    if (isKctUsedElsewhere(draft, item)) { return 'elsewhere'; }
    return 'none';
  }

  // 加入：把該卡的規格落地成帶標記的 rule 併進草稿（就地修改傳入 draft）。
  // 每張卡（含 I 的條件括號）都累積進「作用中」群組（activeEditableGroup）；沒有條件組就新建
  // 一組、組合器預設「全部(AND)」並設為作用中。新規則 join 取該群組現有組合器以維持群組內一致。
  function addKctToDraft(draft, item) {
    var specs = kctRuleSpecs(item);
    if (specs.length === 0) { return; }

    var target = activeEditableGroup(draft);
    if (!target) {
      target = { join: 'AND', matchScope: 'row', rules: [] };
      if (draft.groups.length === 0) { keepMotionKey(EMPTY_FIRST_GROUP, target); }
      draft.groups.push(target);
      setActiveGroup(draft, target);
    }
    var combinator = target.rules.length ? groupCombinator(target) : 'AND';
    specs.forEach(function (spec) {
      var r = materializeRule(spec, item.letter);
      r.join = combinator;
      target.rules.push(r);
    });
  }

  // 取消 KCT：只從「作用中組」移除該字母的 rule（同訊號在別組不動）；不丟棄變空的作用中組
  //（使用者正在編輯它，空組於 toWireScenario 送出時才略過）。只刪自身標記者，使用者手動
  // 加入的同型別條件（無標記）絕不被動到。
  function removeKctFromActiveGroup(draft, item) {
    var active = activeEditableGroup(draft);
    if (!active) { return; }
    announceRemoval(active.rules.filter(function (r) { return r[KCT_LETTER_KEY] === item.letter; }));
    active.rules = active.rules.filter(function (r) { return r[KCT_LETTER_KEY] !== item.letter; });
  }

  // 作用中（active）群組：UI-only 標記 __active，標示「上方面板新增的條件要進哪一組」。恆有至多一個
  // 群組帶 __active；toWireScenario 把 group 重建為 {join, matchScope, rules}，此鍵自然不外洩。
  // 取作用中組：帶 __active 的組 → 否則回退最後一組 → 再無則 null。
  function activeEditableGroup(draft) {
    var groups = draft.groups || [];
    var active = null;
    groups.forEach(function (g) { if (g.__active) { active = g; } });
    if (active) { return active; }
    return groups.length ? groups[groups.length - 1] : null;
  }

  // 設作用中：先清掉所有 __active，再標記目標。傳 null 僅清空。
  function setActiveGroup(draft, group) {
    (draft.groups || []).forEach(function (g) { if (g.__active) { delete g.__active; } });
    if (group) { group.__active = true; }
  }

  // 某組內的 KCT 字母（依 checklist A→J 排序、去重）。
  function kctLettersInGroup(group) {
    var has = {};
    group.rules.forEach(function (r) { if (r[KCT_LETTER_KEY]) { has[r[KCT_LETTER_KEY]] = true; } });
    return Ui.FILTER_KCT_CHECKLIST
      .filter(function (it) { return has[it.letter]; })
      .map(function (it) { return it.letter; });
  }

  // KCT 命名（使用者指定，可手改）：每個條件組一個字母 token（依 checklist 排序、'+' 串；只有自訂條件的
  // 組顯「自訂」、全空略過）。I 和其他 KCT 一樣算進所在組的字母。token 之間以 '｜' 分隔（單一 token
  // 時不加分隔）。同一組另有自訂條件時，token 後接「+自訂」（2026-10-07 使用者裁定）。
  // 例：G、G+H、G+I、G+H｜G+J、G｜H｜I、G｜自訂、G+自訂。
  function kctScenarioName(draft) {
    var tokens = [];
    draft.groups.forEach(function (g) {
      var letters = kctLettersInGroup(g);
      var hasCustom = g.rules.some(function (r) { return !r[KCT_LETTER_KEY]; });
      if (letters.length) { tokens.push(letters.join('+') + (hasCustom ? '+自訂' : '')); }
      else if (g.rules.length) { tokens.push('自訂'); }
    });
    return tokens.join('｜');
  }

  // KCT 動機（使用者指定）：A 到 E 逐字採用評估說明，F 到 J 沿用卡名。
  // 另有自訂條件時，每條自訂條件多一行「自訂：」加上畫面讀回的條件說明（2026-10-07 使用者裁定）。
  function kctScenarioRationale(draft) {
    var lines = Ui.FILTER_KCT_CHECKLIST
      .filter(function (item) { return isKctSelected(draft, item); })
      .map(function (item) { return item.letter + '：' + (item.defaultRationale || item.label); });
    draft.groups.forEach(function (g) {
      g.rules.forEach(function (r) { if (!r[KCT_LETTER_KEY]) { lines.push('自訂：' + ruleSummaryLabel(r, 0)); } });
    });
    return lines.join('\n');
  }

  // KCT 卡帶入的條件是否仍是那張卡：比對卡片規格裡決定條件種類的鍵（型別、預篩選鍵、欄位、比較方式，
  // 以及 I 括號內子條件的種類）。天數、清單、尾數等審計員要填的值不算。B 的兩種借貸組合型別沿用同一張卡。
  var KCT_PAIR_TYPES = ['specialAccountCategoryPair', 'accountPair'];

  function sameKctRuleKind(rule, spec) {
    if (!rule) { return false; }
    var types = KCT_PAIR_TYPES.indexOf(spec.type) >= 0 ? KCT_PAIR_TYPES : [spec.type];
    if (types.indexOf(rule.type) < 0) { return false; }
    if (spec.prescreenKey && rule.prescreenKey !== spec.prescreenKey) { return false; }
    if (spec.field && rule.field !== spec.field) { return false; }
    if (spec.operator && rule.operator !== spec.operator) { return false; }
    if (Array.isArray(spec.rules)) {
      var children = rule.rules || [];
      return children.length === spec.rules.length &&
        spec.rules.every(function (child, i) { return sameKctRuleKind(children[i], child); });
    }
    return true;
  }

  // 條件在列內改成別的條件後，就不再算原本的 KCT 卡：卡片取消勾選，名稱、動機與命名來源跟著改。
  function reconcileKctIdentity(draft) {
    var stripped = false;
    (draft.groups || []).forEach(function (group) {
      group.rules.forEach(function (rule) {
        if (!rule[KCT_LETTER_KEY]) { return; }
        var card = Ui.FILTER_KCT_CHECKLIST.filter(function (item) { return item.letter === rule[KCT_LETTER_KEY]; })[0];
        var spec = card && kctRuleSpecs(card)[0];
        if (!spec || !sameKctRuleKind(rule, spec)) { delete rule[KCT_LETTER_KEY]; stripped = true; }
      });
    });
    return stripped;
  }

  // 自動名稱遇到已儲存的同名情境時附上數字，避免儲存時被名稱不可重複擋下。編輯中的情境不和自己比。
  function uniqueAutomaticName(draft, name) {
    if (!name) { return name; }
    var editing = draft.__editingSavedRef === Store.getState().filter.savedScenarios ? draft.__editingIndex : null;
    var existing = Store.getState().filter.savedScenarios.filter(function (_, i) { return i !== editing; })
      .map(function (scenario) { return String(scenario.name || '').trim().toUpperCase(); });
    var candidate = name;
    for (var suffix = 2; existing.indexOf(candidate.trim().toUpperCase()) >= 0; suffix++) {
      candidate = Array.from(name).slice(0, 60).join('') + ' ' + suffix;
    }
    return candidate;
  }

  // 範例帶入的名稱與動機在條件增減後改回自動（2026-10-07 使用者裁定）；只填值或改值時保留，手改過的欄位不受影響。
  function releaseTemplateMetadata(draft) {
    if (draft.__templateName) { draft.__templateName = false; draft.__nameDirty = false; }
    if (draft.__templateRationale) { draft.__templateRationale = false; draft.__rationaleDirty = false; }
  }

  // 草稿條件改變後的命名收斂點：先確認 KCT 卡帶入的條件仍是原卡，再依目前條件重算自動名稱與動機。
  // structural 表示加入、移除或換掉條件。就地修改傳入 draft。
  // 手改保留（名稱與動機都可以手改）：使用者把欄位改成非空值後（__nameDirty／__rationaleDirty，
  // draft 根層 UI-only 鍵，沿用 __ 慣例；toWireScenario 逐欄挑選 name/rationale/groups，不會外洩），
  // 自動命名不再覆寫該欄；欄位清空即重設旗標、自動命名恢復接手。
  function refreshAutomaticMetadata(draft, structural) {
    var stripped = reconcileKctIdentity(draft);
    if (structural || stripped) { releaseTemplateMetadata(draft); }
    var suggested = automaticMetadata(draft);
    suggested.name = uniqueAutomaticName(draft, suggested.name);
    if (!draft.__nameDirty) { draft.name = suggested.name; }
    if (!draft.__rationaleDirty) { draft.rationale = suggested.rationale; }
    draft.__suggestedMetadata = suggested;
  }

  /* ============================================================================
     區塊 1：KCT 條件選取（可複選 toggle）
     十顆卡片由 FILTER_KCT_CHECKLIST 一份資料驅動。已選/未選兩態由身分標記得出（isKctSelected：
     草稿中是否存在帶該卡字母標記 __kctLetter 的 rule），不再做結構簽章猜測。
     前置條件不足的卡片（依規則需求表，含 GL 沒有配對的欄位）：停用並寫原因，不綁 toggle。
     ============================================================================ */
  function kctPickerHtml(draft) {
    var selectedCount = 0;
    var unavailable = [];
    var cells = Ui.FILTER_KCT_CHECKLIST.map(function (item) {
      // 暫不提供的卡片優先；其次依卡片要加入的條件查規則需求表。
      var requirement = item.disabled ? '' : requirementNote(kctRuleSpecs(item));
      var disabled = item.disabled || !!requirement;
      var note = item.disabled ? item.note : requirement;

      var state = disabled ? 'none' : kctCardState(draft, item);
      if (state === 'selected') { selectedCount++; } // 計數＝作用中組已加入的 KCT

      var cls = 'picker-card picker-card--kct' +
        (state === 'selected' ? ' picker-card--selected' : '') +
        (state === 'elsewhere' ? ' picker-card--elsewhere' : '') +
        (disabled ? ' picker-card--disabled' : '');
      var pressed = state === 'selected' ? 'true' : 'false';

      var card = '<button type="button" class="' + cls + '"' +
          ' data-kct-letter="' + item.letter + '"' +
          ' aria-pressed="' + pressed + '"' +
          (disabled ? ' disabled aria-disabled="true"' : '') + '>' +
        '<span class="picker-card__letter" aria-hidden="true">' + Ui.esc(item.letter) + '</span>' +
        '<span class="picker-card__label" title="' + Ui.esc(item.label) + '">' + Ui.esc(item.label) + '</span>' +
        (item.description ? '<span class="picker-card__note">' + Ui.esc(item.description) + '</span>' : '') +
        (note ? '<span class="picker-card__note">' + Ui.esc(note) + '</span>' : '') +
        (state === 'elsewhere' ? '<span class="picker-card__elsewhere">也在其他組</span>' : '') +
      '</button>';
      if (disabled) { unavailable.push(card); return ''; }
      return card;
    }).join('');

    return (
      '<section class="condition-picker condition-picker--kct">' +
        '<div class="condition-picker__head">' +
          '<h3 class="condition-picker__title">KCT 條件</h3>' +
          '<span class="condition-picker__count">作用中組已選 ' + selectedCount + ' 項</span>' +
        '</div>' +
        '<div class="condition-picker__grid condition-picker__grid--kct">' + cells + '</div>' +
        (unavailable.length ? '<details class="filter-unavailable"' + disclosureAttributes('unavailable-kct') +
          '><summary>尚不可用（' + unavailable.length + '）</summary><div class="condition-picker__grid">' + unavailable.join('') + '</div></details>' : '') +
      '</section>'
    );
  }

  // 自訂篩選條件分頁只有兩區：上面依欄位型別列出欄位按鈕，下面是依審計目的分類的常用查核條件。
  function customPickerHtml() {
    return '<section class="condition-picker condition-picker--custom">' + fieldChipsHtml() + commonChecksHtml() + '</section>';
  }

  // 使用者 2026-10-16 指出審計員習慣直接套用預篩選條件，原本要先加入「沿用第四步預篩選規則」，再到條件卡片的下拉改選，
  // 路徑太長。預篩選條件改成條件總覽的一個分頁，條列每一條規則，按「加入」就帶入那一條；列的樣式沿用常用查核條件。
  function prescreenPickerHtml() {
    var items = Ui.PRESCREEN_RULES.map(function (option) {
      var note = prescreenRequirementNote(option.value);
      var noteId = 'filter-prescreen-note-' + option.value;
      return '<li class="filter-check"><span class="filter-check__label">' + Ui.esc(option.label) + '</span>' +
        '<button type="button" class="btn btn--ghost btn--tiny btn--add" data-action="add-rule" data-add-prescreen="' + option.value + '"' +
          ' aria-label="' + Ui.esc('加入「' + option.label + '」') + '"' + (note ? ' disabled aria-describedby="' + noteId + '"' : '') + '>加入</button>' +
        (note ? '<p class="filter-check__note" id="' + noteId + '">' + Ui.esc(note) + '</p>' : '') + '</li>';
    }).join('');
    return '<section class="condition-picker condition-picker--prescreen">' +
      '<p class="condition-picker__intro">規則與第四步預篩選相同。按「加入」放進目前的條件組，可再和其他條件組合。</p>' +
      '<ul class="filter-check-group__items filter-prescreen-list">' + items + '</ul></section>';
  }

  function addPrescreenCondition(key) {
    if (prescreenRequirementNote(key)) { return; }
    addConditions([conditionFromSpec({ type: 'prescreen', prescreenKey: key })]);
  }

  // 欄位按鈕的三列。核心欄位全部列出；GL 沒有配對的照樣列出但不能按，下方寫出是哪些欄位以及回第三步欄位配對補上
  //（使用者 2026-10-22 裁定）。攸關資料元素欄位依型別接在核心欄位後面。
  var FIELD_CHIP_ROWS = [
    { type: 'date', label: '日期', core: ['postDate', 'docDate'] },
    { type: 'money', label: '數字', core: ['amount'] },
    { type: 'text', label: '文字', core: ['description', 'accNum', 'accName', 'createBy', 'approveBy', 'docNum', 'jeSource', 'lineID'] }
  ];

  // 按鈕文字只寫欄位名稱，和其他欄位同名時才加來源欄名；完整說明放在 title 與 aria-label。
  // attributes 是按鈕的識別屬性（屬性名稱寫到等號前），值是欄位識別；noteId 有值時按鈕停用，並連到說明原因的那一段。
  function fieldButtonHtml(field, fields, attributes, purpose, noteId) {
    var duplicate = fields.filter(function (item) { return item.label === field.label; }).length > 1;
    var text = field.label + (duplicate && field.sourceColumn ? '（' + field.sourceColumn + '）' : '');
    var full = field.extra ? Ui.rdeFieldOptionLabel(field) : field.label;
    return '<button type="button" class="filter-chip' + (field.extra ? ' filter-chip--extra' : '') + '" ' + attributes + '="' + Ui.esc(field.id) + '"' +
      ' title="' + Ui.esc(full) + '" aria-label="' + Ui.esc(purpose + full) + '"' + (noteId ? ' disabled aria-describedby="' + noteId + '"' : '') + '>' +
      Ui.esc(text) + '</button>';
  }

  var FIELD_CHIP_NOTE_ID = 'filter-field-chip-note';

  function fieldChipsHtml() {
    var fields = Ui.FilterValues.fields(Store.getState());
    var unmapped = [];
    var rows = FIELD_CHIP_ROWS.map(function (row) {
      var core = row.core.map(function (id) { return fields.find(function (field) { return !field.extra && field.id === id; }); }).filter(Boolean);
      var extra = fields.filter(function (field) { return field.extra && field.type === row.type; });
      return '<div class="filter-chip-row" role="group" aria-label="' + row.label + '欄位"><span class="filter-chip-row__label">' + row.label + '</span>' +
        '<div class="filter-chip-row__chips">' + core.concat(extra).map(function (field) {
          if (!field.mapped) { unmapped.push({ type: 'fieldValue', field: field.id }); }
          return fieldButtonHtml(field, fields, 'data-action="add-rule" data-add-field', '加入條件：', field.mapped ? '' : FIELD_CHIP_NOTE_ID);
        }).join('') + '</div></div>';
    }).join('');
    var note = unmapped.length ? '<p class="filter-check__note" id="' + FIELD_CHIP_NOTE_ID + '">' + Ui.esc(requirementNote(unmapped)) + '</p>' : '';
    return '<div class="filter-custom-block"><h4 class="filter-custom-block__title">依欄位設定</h4>' + rows + note + '</div>';
  }

  // 常用查核條件依它要加入的條件查規則需求表；查核截止日在建立案件時必填，不另外檢查。
  function commonChecksHtml() {
    var groups = Ui.FILTER_COMMON_CHECK_GROUPS.map(function (group) {
      var open = FilterStep.viewState.checkCategory === group.key;
      var listId = 'filter-checks-' + group.key;
      var items = group.checks.map(function (check) {
        var note = requirementNote(check.rules);
        var noteId = 'filter-check-note-' + check.key;
        return '<li class="filter-check"><span class="filter-check__label">' + Ui.esc(check.label) + '</span>' +
          '<button type="button" class="btn btn--ghost btn--tiny btn--add" data-action="add-rule" data-add-check="' + check.key + '"' +
            ' aria-label="' + Ui.esc('加入「' + check.label + '」') + '"' + (note ? ' disabled aria-describedby="' + noteId + '"' : '') + '>加入</button>' +
          (note ? '<p class="filter-check__note" id="' + noteId + '">' + Ui.esc(note) + '</p>' : '') + '</li>';
      }).join('');
      return '<div class="filter-check-group"><h5 class="filter-check-group__heading">' +
        '<button type="button" class="filter-check-group__toggle" data-check-category="' + group.key + '" aria-expanded="' + open + '" aria-controls="' + listId + '">' +
          Ui.esc(group.label) + '</button></h5>' +
        '<ul class="filter-check-group__items" id="' + listId + '" data-collapsible' + (open ? '' : ' hidden') + '>' + items + '</ul></div>';
    }).join('');
    return '<div class="filter-custom-block"><h4 class="filter-custom-block__title">常用查核條件</h4>' + groups + '</div>';
  }

  // 欄位按鈕帶入的條件：比較方式仍在右側條件卡片上選。人員與科目編號預設完整值清單。
  function fieldConditionRule(fieldId) {
    var field = Ui.FilterValues.fields(Store.getState()).find(function (item) { return item.id === fieldId; });
    if (!field) { return null; }
    var rule = Ui.FilterValues.create(field.type);
    if (['createBy', 'approveBy', 'accNum'].indexOf(field.id) >= 0) { rule.operator = 'in'; }
    delete rule.field; delete rule.fieldId;
    rule[field.extra ? 'fieldId' : 'field'] = field.id;
    if (field.extra && field.type === 'money') { rule.amountBasis = 'signed'; }
    return rule;
  }

  function findCommonCheck(key) {
    var found = null;
    Ui.FILTER_COMMON_CHECK_GROUPS.forEach(function (group) {
      group.checks.forEach(function (check) { if (check.key === key) { found = check; } });
    });
    return found;
  }

  // 第一個前置條件都滿足的預篩選規則。
  function firstUsablePrescreenKey() {
    var usable = availablePrescreenKeys();
    return usable.length ? usable[0].value : 'suspiciousKeywords';
  }

  // 由查核截止日往前算 days 天（含截止日）的起訖日期；沒有查核截止日時留空。
  function periodEndRange(days) {
    var project = Store.getState().project;
    if (!project || !project.periodEnd) { return { from: '', to: '' }; }
    var parts = project.periodEnd.split('-').map(Number);
    var end = new Date(Date.UTC(parts[0], parts[1] - 1, parts[2]));
    var start = new Date(Date.UTC(parts[0], parts[1] - 1, parts[2] - (days - 1)));
    return { from: start.toISOString().slice(0, 10), to: end.toISOString().slice(0, 10) };
  }

  // 把一條規格做成可編輯的條件：欄位條件以該欄位型別的預設值為底，其他型別以 newFilterRule 為底，再套上規格的值。
  function conditionFromSpec(spec) {
    var copy = JSON.parse(JSON.stringify(spec));
    var field = copy.type === 'fieldValue' ? Ui.FilterValues.field(copy, Store.getState()) : null;
    var rule = copy.type === 'fieldValue' ? Ui.FilterValues.create(field ? field.type : 'text') : Ui.newFilterRule(copy.type);
    Object.keys(copy).forEach(function (key) { rule[key] = copy[key]; });
    if (Array.isArray(copy.rules)) { rule.rules = copy.rules.map(conditionFromSpec); }
    if (rule.type === 'prescreen' && !copy.prescreenKey) { rule.prescreenKey = firstUsablePrescreenKey(); }
    if (rule.__periodEndDays) {
      var range = periodEndRange(rule.__periodEndDays);
      rule.from = range.from; rule.to = range.to;
      delete rule.__periodEndDays;
    }
    return rule;
  }

  // 新條件加入目前作用中的組，沒有條件組就先建立。同一傳票的組一律用「且」，其他沿用組內現有的連接方式。
  function addRulesToActiveGroup(draft, rules) {
    var target = activeEditableGroup(draft);
    if (!target) {
      target = { join: 'AND', matchScope: 'row', rules: [] };
      if (draft.groups.length === 0) { keepMotionKey(EMPTY_FIRST_GROUP, target); }
      draft.groups.push(target);
    }
    setActiveGroup(draft, target);
    var join = target.rules.length ? groupCombinator(target) : 'AND';
    rules.forEach(function (rule) { rule.join = join; target.rules.push(rule); });
    return target;
  }

  function addConditions(rules, focusValue) {
    if (!rules || !rules.length) { return; }
    var draft = Store.getState().filter.draft;
    addRulesToActiveGroup(draft, rules);
    revealAddedRule(rules[rules.length - 1], focusValue);
    refreshAutomaticMetadata(draft, true);
    Store.setFilterDraft(draft);
  }

  function addFieldCondition(fieldId) {
    var rule = fieldConditionRule(fieldId);
    if (rule && !ownRequirementNote(rule)) { addConditions([rule]); }
  }

  function addCommonCheck(key) {
    var check = findCommonCheck(key);
    if (!check || requirementNote(check.rules)) { return; }
    addConditions(check.rules.map(conditionFromSpec), !!check.focusValue);
  }

  function disclosureAttributes(key) {
    return ' data-filter-disclosure="' + key + '"' + (FilterStep.viewState.disclosures[key] ? ' open' : '');
  }

  // 建議的名稱與動機只描述已選條件，不自行編出風險判斷。有 KCT 卡時用 KCT 命名，沒有條件時兩欄留空。
  function automaticMetadata(draft) {
    if (hasKctMarker(draft)) { return { name: kctScenarioName(draft), rationale: kctScenarioRationale(draft) }; }
    var rules = draft.groups.reduce(function (all, group) { return all.concat(group.rules); }, []);
    if (!rules.length) { return { name: '', rationale: '' }; }
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
    var expression = readBackPlainText(readBackExpressionHtml(draft, true));
    var rationale = expression ? '檢視符合以下條件的分錄：' + expression + '。' : '';
    if (Array.from(rationale).length > 180) {
      var description = topics.join('；');
      if (Array.from(description).length > 130) { description = topics.slice(0, 2).join('；') + '等' + rules.length + '項條件'; }
      rationale = '依已設定的條件組合篩選分錄，檢視' + description + '。';
    }
    return { name: name, rationale: rationale };
  }

  // 值編輯與重繪後把自動名稱與動機同步到草稿和畫面；手寫或範例帶入的文字由 refreshAutomaticMetadata 保留。
  function syncSuggestedMetadata(container) {
    var draft = Store.getState().filter.draft;
    // 正常路徑已在改條件時收斂；這裡只是保險。卡片身分改變時排一次重繪，讓卡片勾選與條件一致；
    // 不在綁定事件途中同步重繪。
    if (reconcileKctIdentity(draft)) {
      refreshAutomaticMetadata(draft, true);
      global.setTimeout(function () { Store.touch(); }, 0);
      return;
    }
    var oldName = draft.name, oldRationale = draft.rationale;
    refreshAutomaticMetadata(draft, false);
    if (oldName !== draft.name || oldRationale !== draft.rationale) {
      Store.patchFilterDraftMeta({ name: draft.name, rationale: draft.rationale, __suggestedMetadata: draft.__suggestedMetadata });
      var nameInput = container.querySelector('[data-bind="scenario-name"]');
      var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
      if (nameInput) { nameInput.value = draft.name || ''; }
      if (rationaleInput) { rationaleInput.value = draft.rationale || ''; }
      var heading = container.querySelector('[data-bind="editing-scenario-name"]');
      if (heading) { heading.textContent = '編輯情境：' + (draft.name || ''); }
    }
  }

  // 「從範例開始」：範例清單只列名稱，動機放在 title。前置條件不足時停用並寫一句原因。
  function textRdeFields() {
    return Ui.FilterValues.fields(Store.getState()).filter(function (field) { return field.extra && field.type === 'text'; });
  }

  function templateRequirementNote(template) {
    var note = requirementNote(template.groups.reduce(function (rules, group) { return rules.concat(group.rules); }, []));
    if (!note && template.pickField === 'text' && textRdeFields().length === 0) { note = '需要先在欄位配對加入部門等文字欄位'; }
    return note;
  }

  function exampleListHtml() {
    var fields = Ui.FilterValues.fields(Store.getState());
    var items = Ui.FILTER_SCENARIO_TEMPLATES.map(function (template) {
      var note = templateRequirementNote(template);
      var noteId = 'filter-example-note-' + template.key;
      var picking = !note && FilterStep.viewState.examplePick === template.key;
      var pick = picking
        ? '<div class="filter-example__pick" role="group" data-example-pick="' + template.key + '" data-motion-key="example-pick-' + template.key + '" aria-label="' + Ui.esc('選擇「' + template.label + '」要用的欄位') + '">' +
            '<span>選擇欄位：</span>' + textRdeFields().map(function (field) {
              return fieldButtonHtml(field, fields, 'data-example-field', '使用欄位：');
            }).join('') +
            '<button type="button" class="btn btn--ghost btn--tiny" data-action="cancel-example-pick">取消</button></div>'
        : '';
      return '<li class="filter-example" data-motion-key="example-' + template.key + '">' +
        '<button type="button" class="filter-example__apply" data-template-key="' + template.key + '" title="' + Ui.esc(template.rationale) + '"' +
          (note ? ' disabled aria-describedby="' + noteId + '"' : ' data-action="apply-template"') + (picking ? ' aria-expanded="true"' : '') + '>' +
          Ui.esc(template.label) + '</button>' +
        (note ? '<p class="filter-example__note" id="' + noteId + '">' + Ui.esc(note) + '</p>' : '') + pick + '</li>';
    }).join('');
    return '<p class="filter-example-list__hint">套用後會替換目前的條件，已儲存的情境不受影響。</p><ul class="filter-example-list__items">' + items + '</ul>';
  }

  // 範例要用文字型攸關資料元素欄位時：只有一個就直接帶入，兩個以上先就地列出欄位讓審計員選。
  // 停用或還沒選欄位時不改動草稿。
  function chooseTemplate(container, key) {
    var template = Ui.FILTER_SCENARIO_TEMPLATES.filter(function (item) { return item.key === key; })[0];
    if (!template || templateRequirementNote(template)) { return; }
    if (template.pickField === 'text') {
      var choices = textRdeFields();
      if (choices.length > 1) {
        FilterStep.viewState.examplePick = key;
        redrawExampleList(container);
        if (global.JetFocus) { global.JetFocus.defer(function () { return container.querySelector('[data-example-pick] [data-example-field]'); }); }
        return;
      }
      requestTemplate(key, choices[0].id);
      return;
    }
    requestTemplate(key);
  }

  // 範例會換掉整份草稿；要選欄位的範例在選好欄位、真的要換掉時才詢問。
  function requestTemplate(key, fieldId) {
    var template = Ui.FILTER_SCENARIO_TEMPLATES.filter(function (item) { return item.key === key; })[0];
    if (!template) { return; }
    guardDraftReplacement('template', template.label, function () { applyTemplate(key, fieldId); });
  }

  function cancelExamplePick(container) {
    var key = FilterStep.viewState.examplePick;
    FilterStep.viewState.examplePick = null;
    redrawExampleList(container);
    if (global.JetFocus) { global.JetFocus.defer(function () { return container.querySelector('[data-template-key="' + key + '"]'); }); }
  }

  // 選欄位的那一列從範例下面長出來、取消時收回去（使用者 2026-10-13 要求展開與收合一致）。
  function redrawExampleList(container) {
    var list = Ui.findLive(container, '[data-example-list]');
    if (!list) { return; }
    fillSmoothly(list, function () { list.innerHTML = exampleListHtml(); });
    bindExampleItems(container);
  }

  function bindExampleItems(container) {
    container.querySelectorAll('[data-action="apply-template"]').forEach(function (button) {
      button.addEventListener('click', function () { chooseTemplate(container, button.getAttribute('data-template-key')); });
    });
    container.querySelectorAll('[data-example-field]').forEach(function (button) {
      button.addEventListener('click', function () { requestTemplate(FilterStep.viewState.examplePick, button.getAttribute('data-example-field')); });
    });
    var cancel = container.querySelector('[data-action="cancel-example-pick"]');
    if (cancel) { cancel.addEventListener('click', function () { cancelExamplePick(container); }); }
  }

  function bindExampleList(container) {
    var toggle = container.querySelector('[data-action="toggle-examples"]');
    var list = container.querySelector('[data-example-list]');
    if (!toggle || !list) { return; }
    function setOpen(open) {
      var picking = !!FilterStep.viewState.examplePick;
      FilterStep.viewState.examplesOpen = open;
      if (!open) { FilterStep.viewState.examplePick = null; }
      list.hidden = !open;
      toggle.setAttribute('aria-expanded', String(open));
      // 收起時正在選欄位的那一列也拿掉，下次打開不會留著按了沒有作用的欄位按鈕。
      if (!open && picking) { redrawExampleList(container); }
    }
    toggle.addEventListener('click', function () { setOpen(!FilterStep.viewState.examplesOpen); });
    // Esc 先取消正在選的欄位；沒有在選欄位時收起清單，焦點回到「從範例開始」。草稿都不動。
    list.addEventListener('keydown', function (event) {
      if (event.key !== 'Escape') { return; }
      if (FilterStep.viewState.examplePick) { cancelExamplePick(container); return; }
      setOpen(false);
      toggle.focus();
    });
    bindExampleItems(container);
  }

  // 把範例落地成草稿：規則深拷貝，名稱與動機先當固定文字，條件增減後才改回自動。
  // 帶 kct 字母的範例先用 addKctToDraft 帶入該卡，範例第一組的條件接在同一組 KCT 條件之後
  //（例如「非營業日且排除指定日期」是第 1 組第 1 條非營業日、第 2 條排除日期），其餘組另開；
  // 期末 N 天由查核截止日算起訖。fieldId 空白的條件換成 fieldId（審計員選的文字型攸關資料元素欄位）。
  // 套用後若有條件還要填值，焦點移到第一個要填的欄位，但不代填任何值。
  function applyTemplate(key, fieldId) {
    var template = Ui.FILTER_SCENARIO_TEMPLATES.filter(function (item) { return item.key === key; })[0];
    if (!template) { return; }
    var draft = { name: template.label, rationale: template.rationale, groups: [], __nameDirty: true, __rationaleDirty: true,
      __templateName: true, __templateRationale: true };
    draft.name = uniqueAutomaticName(draft, draft.name);
    if (template.kct) {
      var card = Ui.FILTER_KCT_CHECKLIST.filter(function (item) { return item.letter === template.kct; })[0];
      if (card) { addKctToDraft(draft, card); }
    }
    function prepare(rule) {
      if (rule.__periodEndDays) {
        var range = periodEndRange(rule.__periodEndDays);
        rule.from = range.from; rule.to = range.to;
      }
      delete rule.__periodEndDays;
      if (rule.type === 'fieldValue' && rule.fieldId === '' && fieldId) { rule.fieldId = fieldId; delete rule.field; }
      if (Array.isArray(rule.rules)) { rule.rules.forEach(prepare); }
      return rule;
    }
    var kctGroup = template.kct ? activeEditableGroup(draft) : null;
    template.groups.forEach(function (group, index) {
      var copy = { join: group.join || 'AND', matchScope: group.matchScope || 'row', rules: group.rules.map(function (rule) {
        return prepare(JSON.parse(JSON.stringify(rule)));
      }) };
      // 併進 KCT 那一組只在語意不變時做：範例這一組原本以「且」接在 KCT 組之後，組內只有一條或全部以「且」連接。
      // 組內有「或」時併進去會把「I 且 (a 或 b)」變成「(I 且 a) 或 b」，所以照原樣另開一組。
      var sameMeaning = (copy.join || 'AND') === 'AND' && (copy.rules.length === 1 || groupCombinator(copy) === 'AND');
      if (index === 0 && kctGroup && sameMeaning && groupMatchScope(kctGroup) === groupMatchScope(copy)) {
        copy.rules.forEach(function (rule, ri) {
          if (ri === 0) { rule.join = kctGroup.rules.length ? groupCombinator(kctGroup) : 'AND'; }
          kctGroup.rules.push(rule);
        });
        setActiveGroup(draft, kctGroup);
        return;
      }
      draft.groups.push(copy);
      setActiveGroup(draft, copy);
    });
    FilterStep.viewState.lastSavedName = null;
    FilterStep.viewState.examplesOpen = false;
    FilterStep.viewState.examplePick = null;
    // 畫面上馬上看得到結果，不另記訊息（使用者 2026-10-13 勾選頁確認刪除 53）。
    Store.setFilterDraft(draft);
    var pending = draftRuleProblems(draft)[0];
    if (global.JetFocus) { global.JetFocus.defer(function () {
      return (pending && ruleValueControl(pending.gi, pending.path)) ||
        document.querySelector('.filter-workspace [data-builder-title]');
    }); }
  }

  function sourceButton(key, label) {
    return '<button type="button" data-condition-source="' + key + '"' +
      Ui.tabAttributes(FilterStep.viewState.conditionSource === key, 'condition-pane-' + key, 'condition-tab-' + key) + '>' + Ui.esc(label) + '</button>';
  }

  function conditionPaneAttributes(key) {
    return ' data-condition-pane="' + key + '" id="condition-pane-' + key + '" role="tabpanel" aria-labelledby="condition-tab-' + key + '"' +
      ' data-motion-pane' + (FilterStep.viewState.conditionSource === key ? '' : ' hidden');
  }

  // 條件來源：欄位按鈕、常用查核條件、「預篩選」分頁、子條件組的加入按鈕、範例清單與 KCT 卡（原本 bind 裡的兩段）。
  function bindConditionSources(container) {
    // 自訂篩選條件：欄位按鈕與常用查核條件都加入目前作用中的組。
    container.querySelectorAll('[data-add-field]').forEach(function (button) {
      button.addEventListener('click', function () { addFieldCondition(button.getAttribute('data-add-field')); });
    });
    container.querySelectorAll('[data-add-check]').forEach(function (button) {
      button.addEventListener('click', function () { addCommonCheck(button.getAttribute('data-add-check')); });
    });
    container.querySelectorAll('[data-add-prescreen]').forEach(function (button) {
      button.addEventListener('click', function () { addPrescreenCondition(button.getAttribute('data-add-prescreen')); });
    });
    // 常用查核條件的分類一次只展開一類；只切換畫面，不重繪也不改草稿。
    container.querySelectorAll('[data-check-category]').forEach(function (button) {
      button.addEventListener('click', function () {
        var key = button.getAttribute('data-check-category');
        FilterStep.viewState.checkCategory = FilterStep.viewState.checkCategory === key ? null : key;
        container.querySelectorAll('[data-check-category]').forEach(function (toggle) {
          var open = toggle.getAttribute('data-check-category') === FilterStep.viewState.checkCategory;
          toggle.setAttribute('aria-expanded', String(open));
          var list = container.querySelector('#' + toggle.getAttribute('aria-controls'));
          if (list) { list.hidden = !open; }
        });
      });
    });
    // 在這一組加入子條件組或整張傳票條件；新草稿畫面上的空白第 1 組在這時才建立。
    container.querySelectorAll('[data-add-compound]').forEach(function (button) {
      button.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var group = draft.groups[Number(button.getAttribute('data-gi'))];
        if (!group) {
          group = { join: 'AND', matchScope: 'row', rules: [] };
          if (draft.groups.length === 0) { keepMotionKey(EMPTY_FIRST_GROUP, group); }
          draft.groups.push(group);
        }
        setActiveGroup(draft, group);
        addConditions([Ui.newFilterRule(button.getAttribute('data-add-compound'))]);
      });
    });
    bindExampleList(container);
    // KCT 條件卡可複選 toggle —— 已選則移除、未選則加入；累積成同一情境。
    // 停用卡（B/未匯入科目配對）不綁事件。加入/移除/已選偵測全收斂於上方少數深函式。
    // 畫面上馬上看得到結果，不另記加入或移除的訊息（使用者 2026-10-13 勾選頁確認刪除 56、57）。
    container.querySelectorAll('.picker-card--kct').forEach(function (btn) {
      if (btn.disabled) { return; }
      btn.addEventListener('click', function () {
        var item = Ui.FILTER_KCT_CHECKLIST.filter(function (k) {
          return k.letter === btn.getAttribute('data-kct-letter');
        })[0];
        if (!item) { return; }
        var draft = Store.getState().filter.draft;
        if (isKctSelectedActive(draft, item)) {
          // 取消：只從「作用中組」移除（同訊號在別組不動）。
          removeKctFromActiveGroup(draft, item);
          refreshAutomaticMetadata(draft, true);
          Store.setFilterDraft(draft);
        } else {
          addKctToDraft(draft, item); // 每張卡（含 I 的條件括號）都加入作用中組
          var targetGroup = activeEditableGroup(draft);
          var addedRule = targetGroup && targetGroup.rules.find(function (rule) { return rule[KCT_LETTER_KEY] === item.letter; });
          if (addedRule) { revealAddedRule(addedRule); }
          refreshAutomaticMetadata(draft, true);
          Store.setFilterDraft(draft);
        }
      });
    });

  }

  // 掛到共用的 FilterStep，供其他檔案與測試取用。
  FilterStep.isPatternMenuKctType = isPatternMenuKctType;
  FilterStep.availableKctTypes = availableKctTypes;
  FilterStep.UNMAPPED_OPTION_SUFFIX = UNMAPPED_OPTION_SUFFIX;
  FilterStep.rdeFieldOptions = rdeFieldOptions;
  FilterStep.rdeFieldValueType = rdeFieldValueType;
  FilterStep.availablePrescreenKeys = availablePrescreenKeys;
  FilterStep.RETIRED_TYPE_NOTE = RETIRED_TYPE_NOTE;
  FilterStep.prescreenRequirementNote = prescreenRequirementNote;
  FilterStep.KCT_LETTER_KEY = KCT_LETTER_KEY;
  FilterStep.OUTPUT_ANCHOR_LABEL = OUTPUT_ANCHOR_LABEL;
  FilterStep.SAME_VOUCHER_EXPLANATION = SAME_VOUCHER_EXPLANATION;
  FilterStep.hasKctMarker = hasKctMarker;
  FilterStep.scenarioSource = scenarioSource;
  FilterStep.requiresScenarioMetadata = requiresScenarioMetadata;
  FilterStep.kctRuleSpecs = kctRuleSpecs;
  FilterStep.materializeRule = materializeRule;
  FilterStep.isKctSelected = isKctSelected;
  FilterStep.isKctSelectedActive = isKctSelectedActive;
  FilterStep.isKctUsedElsewhere = isKctUsedElsewhere;
  FilterStep.kctCardState = kctCardState;
  FilterStep.addKctToDraft = addKctToDraft;
  FilterStep.removeKctFromActiveGroup = removeKctFromActiveGroup;
  FilterStep.activeEditableGroup = activeEditableGroup;
  FilterStep.setActiveGroup = setActiveGroup;
  FilterStep.kctLettersInGroup = kctLettersInGroup;
  FilterStep.kctScenarioName = kctScenarioName;
  FilterStep.kctScenarioRationale = kctScenarioRationale;
  FilterStep.KCT_PAIR_TYPES = KCT_PAIR_TYPES;
  FilterStep.sameKctRuleKind = sameKctRuleKind;
  FilterStep.reconcileKctIdentity = reconcileKctIdentity;
  FilterStep.uniqueAutomaticName = uniqueAutomaticName;
  FilterStep.releaseTemplateMetadata = releaseTemplateMetadata;
  FilterStep.refreshAutomaticMetadata = refreshAutomaticMetadata;
  FilterStep.kctPickerHtml = kctPickerHtml;
  FilterStep.customPickerHtml = customPickerHtml;
  FilterStep.prescreenPickerHtml = prescreenPickerHtml;
  FilterStep.addPrescreenCondition = addPrescreenCondition;
  FilterStep.FIELD_CHIP_ROWS = FIELD_CHIP_ROWS;
  FilterStep.fieldButtonHtml = fieldButtonHtml;
  FilterStep.FIELD_CHIP_NOTE_ID = FIELD_CHIP_NOTE_ID;
  FilterStep.fieldChipsHtml = fieldChipsHtml;
  FilterStep.commonChecksHtml = commonChecksHtml;
  FilterStep.fieldConditionRule = fieldConditionRule;
  FilterStep.findCommonCheck = findCommonCheck;
  FilterStep.firstUsablePrescreenKey = firstUsablePrescreenKey;
  FilterStep.periodEndRange = periodEndRange;
  FilterStep.conditionFromSpec = conditionFromSpec;
  FilterStep.addRulesToActiveGroup = addRulesToActiveGroup;
  FilterStep.addConditions = addConditions;
  FilterStep.addFieldCondition = addFieldCondition;
  FilterStep.addCommonCheck = addCommonCheck;
  FilterStep.disclosureAttributes = disclosureAttributes;
  FilterStep.automaticMetadata = automaticMetadata;
  FilterStep.syncSuggestedMetadata = syncSuggestedMetadata;
  FilterStep.textRdeFields = textRdeFields;
  FilterStep.templateRequirementNote = templateRequirementNote;
  FilterStep.exampleListHtml = exampleListHtml;
  FilterStep.chooseTemplate = chooseTemplate;
  FilterStep.requestTemplate = requestTemplate;
  FilterStep.cancelExamplePick = cancelExamplePick;
  FilterStep.redrawExampleList = redrawExampleList;
  FilterStep.bindExampleItems = bindExampleItems;
  FilterStep.bindExampleList = bindExampleList;
  FilterStep.applyTemplate = applyTemplate;
  FilterStep.sourceButton = sourceButton;
  FilterStep.conditionPaneAttributes = conditionPaneAttributes;
  FilterStep.bindConditionSources = bindConditionSources;
})(window);
