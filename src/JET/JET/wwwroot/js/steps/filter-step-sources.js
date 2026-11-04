/*
  第五步：進階條件篩選 —— 條件來源的挑選。
  常用篩選條件的卡片、「預篩選」分頁、欄位條件分頁的欄位與常用查法、右側的情境範例，把條件加進作用中的條件組，以及自動產生的情境名稱與動機。
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
  var readBackExpressionHtml = forward('readBackExpressionHtml');
  var readBackPlainText = forward('readBackPlainText');
  var guardDraftReplacement = forward('guardDraftReplacement');
  var ruleSummaryLabel = forward('ruleSummaryLabel');
  var draftRuleProblems = forward('draftRuleProblems');
  var ruleValueControl = forward('ruleValueControl');
  // 其他檔案定義的常數：只讀，定義它的檔案先載入。
  var EMPTY_FIRST_GROUP = FilterStep.EMPTY_FIRST_GROUP;

  // 樣態選單裡常用篩選條件專用的型別；前置條件不足的不列出，已選的條件不能用時原因寫在卡片上（見 ruleSubjectHtml）。
  function isPatternMenuCommonType(type) {
    return Ui.FILTER_RULE_TYPES.some(function (t) { return t.value === type && t.group === 'common'; });
  }

  function availableCommonTypes() {
    return Ui.FILTER_RULE_TYPES.filter(function (t) { return isPatternMenuCommonType(t.value) && !requirementNote([{ type: t.value }]); });
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

  // 預篩選條件還缺什麼；鍵已停用時和後端同一句（裁定 2026-10-14-01）。
  function prescreenRequirementNote(key) {
    var known = Ui.PRESCREEN_RULES.some(function (item) { return item.value === key; });
    return known ? requirementNote([{ type: 'prescreen', prescreenKey: key }]) : RETIRED_TYPE_NOTE;
  }

  /* ============================================================================
     常用篩選條件的卡片和草稿條件之間的對應，集中在下面少數幾個函式處理，其他程式只呼叫它們。
     一張卡 = 一份「rule 規格」陣列（commonRuleSpecs）。加入 = 把這些規格化成真 rule、
     在每條 rule 上打一個穩定的身分標記（__commonCard = 卡片代號）併進草稿；已選偵測 =
     草稿中是否存在帶該卡標記的 rule；移除 = 把所有帶該卡標記的 rule 挑掉。

     為何用標記而非結構簽章（取捨顯式）：先前以 type(+field/mode/prescreenKey) 的結構簽章
     猜「這條 rule 是不是這張卡帶進來的」。但卡片的簽章會與查核員「自訂」的同型別條件
     重疊——例如特定摘要卡會被任何手動加入的同型別關鍵字條件誤判為「已選」，且移除
     時只刪「第一條」同簽章 rule，可能誤刪使用者手動加入的條件。改用精確身分標記後，這一整類
     誤判／誤刪的 edge case 直接消失：自訂的同型別條件不帶標記，永遠不會被當成卡片帶入的條件。

     __commonCard 是 UI-only 標記（鍵名以雙底線開頭、明示「內部用途、不屬 wire 契約」），絕不可
     送進後端。所有送出點一律經 toWireScenario() 深拷貝並遞迴剝除；有卡片標記或已存
     source:'common' 時另送根層 source:'common'，groups 內每條 rule 不含任何 UI-only 鍵。
     ============================================================================ */

  // rule 身分標記鍵：標示「這條 rule 是哪張卡帶進來的」，值是卡片代號（FILTER_COMMON_CONDITIONS 的 key）。
  // UI-only，絕不送 wire（由 toWireScenario 剝除）。集中為常數，讓打標記／偵測／剝除三處引用同一事實。
  var COMMON_CARD_KEY = '__commonCard';

  // 案件檔記錄「條件來自常用篩選條件」的來源代碼（wire scenario.source），鏡像後端 FilterScenarioSources.Common。
  var COMMON_SOURCE = 'common';

  // 草稿目前是否仍含卡片的身分標記。來源代碼與自動動機只信任此即時狀態；最後一張卡的條件被移除或改型後，
  // 不會留下過期的根層 source。
  function hasCommonMarker(scenario) {
    return (scenario.groups || []).some(function (group) {
      return (group.conditions || []).some(function (rule) { return !!rule[COMMON_CARD_KEY]; });
    });
  }

  // Wire source 的單一推導點：草稿讀標記；project.load 的 saved summary 已沒有標記，故保留 canonical source。
  // 2026-10-29 以前儲存的情境記的是另一個來源代碼，這一版不認得，一律當成審計員自訂的情境（見 filter-step-saved 的 editScenario）。
  function scenarioSource(scenario) {
    return hasCommonMarker(scenario) || (scenario.source === COMMON_SOURCE && !scenario.__restoredOrigins) ? COMMON_SOURCE : null;
  }

  // 一張卡會建立的「rule 規格」陣列（純資料；type 必填，其餘為覆寫鍵）。
  //   kind:'type'   → 單一規格 { type: ref }，落地為 newFilterCondition(ref)。
  //   kind:'preset' → 沿用 FILTER_COMMON_PRESETS 的單一 overrides 規格。
  function commonRuleSpecs(item) {
    if (item.kind === 'spec') { return [item.overrides]; }
    if (item.kind === 'preset') {
      var preset = Ui.FILTER_COMMON_PRESETS.filter(function (p) { return p.key === item.ref; })[0];
      return preset ? [preset.overrides] : [];
    }
    return item.ref ? [{ type: item.ref }] : [];
  }

  // 把一條規格落地成真 rule，並打上該卡的身分標記：以 newFilterCondition(type) 為底，套上規格的
  // 覆寫鍵（field/mode/prescreenKey/join/conditions…），最後標 __commonCard = 卡片代號（UI-only，剝除後才送 wire）。
  // 規格先整份深拷貝：特定摘要的關鍵字清單這類陣列，多次加入不得共用同一份。
  function materializeRule(spec, cardKey) {
    var copy = JSON.parse(JSON.stringify(spec));
    var rule = Ui.newFilterCondition(copy.type);
    Object.keys(copy).forEach(function (k) { rule[k] = copy[k]; });
    rule[COMMON_CARD_KEY] = cardKey;
    return rule;
  }

  // 已選偵測：草稿中是否存在帶該卡標記的 rule。純比對身分標記，不再做結構簽章猜測，
  // 故使用者自訂的同型別條件（不帶標記）永遠不會誤觸已選。空規格＝停用卡，不算已選。
  function isCommonSelected(draft, item) {
    if (commonRuleSpecs(item).length === 0) { return false; }
    return draft.groups.some(function (g) {
      return g.conditions.some(function (r) { return r[COMMON_CARD_KEY] === item.key; });
    });
  }

  // 卡片「已選」判定（組層級）：看「作用中組」是否含該卡。讓同一訊號可在不同組各自存在，
  // 且在作用中組 toggle 只動該組（避免切換卡片時誤刪其他組的同一訊號）。
  function isCommonSelectedActive(draft, item) {
    if (commonRuleSpecs(item).length === 0) { return false; }
    var active = activeEditableGroup(draft);
    return !!active && active.conditions.some(function (r) { return r[COMMON_CARD_KEY] === item.key; });
  }

  // 卡片是否「用於其他（非作用中）條件組」——給卡片加淡標記「也在其他組」，維持全局意識。
  function isCommonUsedElsewhere(draft, item) {
    var active = activeEditableGroup(draft);
    return draft.groups.some(function (g) {
      return g !== active && g.conditions.some(function (r) { return r[COMMON_CARD_KEY] === item.key; });
    });
  }

  // 卡片視覺狀態（給 commonCardsHtml）：
  //   'selected'  = 在作用中組（藍色高亮）。
  //   'elsewhere' = 只在別的（非作用中）組（藍邊「也在其他組」）。
  //   'none'      = 未加入。
  function commonCardState(draft, item) {
    if (isCommonSelectedActive(draft, item)) { return 'selected'; }
    if (isCommonUsedElsewhere(draft, item)) { return 'elsewhere'; }
    return 'none';
  }

  // 加入：把該卡的規格落地成帶標記的 rule 併進草稿（就地修改傳入 draft）。
  // 每張卡都累積進「作用中」群組（activeEditableGroup）；沒有條件組就新建
  // 一組、組合器預設「全部(AND)」並設為作用中。新規則 join 取該群組現有組合器以維持群組內一致。
  function addCommonToDraft(draft, item) {
    var specs = commonRuleSpecs(item);
    if (specs.length === 0) { return; }

    var target = activeEditableGroup(draft);
    if (!target) {
      target = { join: 'AND', matchScope: 'row', conditions: [] };
      if (draft.groups.length === 0) { keepMotionKey(EMPTY_FIRST_GROUP, target); }
      draft.groups.push(target);
      setActiveGroup(draft, target);
    }
    var combinator = target.conditions.length ? groupCombinator(target) : 'AND';
    specs.forEach(function (spec) {
      var r = materializeRule(spec, item.key);
      r.join = combinator;
      target.conditions.push(r);
    });
  }

  // 取消卡片：只從「作用中組」移除該卡的 rule（同訊號在別組不動）；不丟棄變空的作用中組
  //（使用者正在編輯它，空組於 toWireScenario 送出時才略過）。只刪自身標記者，使用者手動
  // 加入的同型別條件（無標記）絕不被動到。
  function removeCommonFromActiveGroup(draft, item) {
    var active = activeEditableGroup(draft);
    if (!active) { return; }
    announceRemoval(active.conditions.filter(function (r) { return r[COMMON_CARD_KEY] === item.key; }));
    active.conditions = active.conditions.filter(function (r) { return r[COMMON_CARD_KEY] !== item.key; });
  }

  // 作用中（active）群組：UI-only 標記 __active，標示「上方面板新增的條件要進哪一組」。恆有至多一個
  // 群組帶 __active；toWireScenario 把 group 重建為 {join, matchScope, conditions}，此鍵自然不外洩。
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

  // 條件上的身分標記對應的卡片；不是卡片帶入的條件回傳 undefined。
  function cardOfRule(rule) {
    var key = rule && rule[COMMON_CARD_KEY];
    return key ? Ui.FILTER_COMMON_CONDITIONS.filter(function (item) { return item.key === key; })[0] : undefined;
  }

  // 有卡片時的動機只列評估說明（裁定 2026-10-30-02）：依卡片順序每張一行，沒有評估說明的卡片寫卡片名稱。
  // 另有自訂條件時，每條自訂條件多一行「自訂：」加上畫面讀回的條件說明（裁定 2026-10-07-06）。
  // 卡片字母已拿掉（裁定 2026-10-29-11），每行不再以字母開頭。
  function commonScenarioRationale(draft) {
    var lines = Ui.FILTER_COMMON_CONDITIONS
      .filter(function (item) { return isCommonSelected(draft, item); })
      .map(function (item) { return item.defaultRationale || item.label; });
    draft.groups.forEach(function (g) {
      g.conditions.forEach(function (r) { if (!r[COMMON_CARD_KEY]) { lines.push('自訂：' + ruleSummaryLabel(r, 0)); } });
    });
    return lines.join('\n');
  }

  // 卡片帶入的條件是否仍是那張卡：比對卡片規格裡決定條件種類的鍵（型別、預篩選鍵、欄位、比較方式，
  // 規格是子條件組時再逐條比對子條件的種類）。天數、清單、尾數等審計員要填的值不算。固定資產與費用的兩種借貸組合型別沿用同一張卡。
  var COMMON_PAIR_TYPES = ['specialAccountCategoryPair', 'accountPair'];

  function sameCommonRuleKind(rule, spec) {
    if (!rule) { return false; }
    var types = COMMON_PAIR_TYPES.indexOf(spec.type) >= 0 ? COMMON_PAIR_TYPES : [spec.type];
    if (types.indexOf(rule.type) < 0) { return false; }
    if (spec.prescreenKey && rule.prescreenKey !== spec.prescreenKey) { return false; }
    if (spec.field && rule.field !== spec.field) { return false; }
    if (spec.operator && rule.operator !== spec.operator) { return false; }
    if (Array.isArray(spec.conditions)) {
      var children = rule.conditions || [];
      return children.length === spec.conditions.length &&
        spec.conditions.every(function (child, i) { return sameCommonRuleKind(children[i], child); });
    }
    return true;
  }

  // 卡片認得的條件種類：現在加入的規格，加上已儲存情境裡的舊寫法（FILTER_COMMON_PRESETS 的 legacyOverrides）。
  // 舊寫法照舊認得是這張卡（裁定 2026-11-03-09）；重新按卡片時加入的是現在的規格。
  function commonRuleKinds(card) {
    var specs = commonRuleSpecs(card).slice(0, 1);
    if (!specs.length || card.kind !== 'preset') { return specs; }
    var preset = Ui.FILTER_COMMON_PRESETS.filter(function (p) { return p.key === card.ref; })[0];
    return specs.concat(preset && preset.legacyOverrides || []);
  }

  // 條件在列內改成別的條件後，就不再算原本的卡片：卡片取消勾選，名稱、動機與命名來源跟著改。
  function reconcileCommonIdentity(draft) {
    var stripped = false;
    (draft.groups || []).forEach(function (group) {
      group.conditions.forEach(function (rule) {
        if (!rule[COMMON_CARD_KEY]) { return; }
        var card = cardOfRule(rule);
        var kinds = card ? commonRuleKinds(card) : [];
        if (!kinds.some(function (spec) { return sameCommonRuleKind(rule, spec); })) { delete rule[COMMON_CARD_KEY]; stripped = true; }
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

  // 範例帶入的名稱與動機在條件增減後改回自動（裁定 2026-10-07-07）；只填值或改值時保留，手改過的欄位不受影響。
  function releaseTemplateMetadata(draft) {
    if (draft.__templateName) { draft.__templateName = false; draft.__nameDirty = false; }
    if (draft.__templateRationale) { draft.__templateRationale = false; draft.__rationaleDirty = false; }
  }

  // 草稿條件改變後的命名收斂點：先確認卡片帶入的條件仍是原卡，再依目前條件重算自動名稱與動機。
  // structural 表示加入、移除或換掉條件。就地修改傳入 draft。
  // 手改保留（名稱與動機都可以手改）：使用者把欄位改成非空值後（__nameDirty／__rationaleDirty，
  // draft 根層 UI-only 鍵，沿用 __ 慣例；toWireScenario 逐欄挑選 name/rationale/groups，不會外洩），
  // 自動命名不再覆寫該欄；欄位清空即重設旗標、自動命名恢復接手。
  function refreshAutomaticMetadata(draft, structural) {
    var stripped = reconcileCommonIdentity(draft);
    if (structural || stripped) { releaseTemplateMetadata(draft); }
    var suggested = automaticMetadata(draft);
    suggested.name = uniqueAutomaticName(draft, suggested.name);
    if (!draft.__nameDirty) { draft.name = suggested.name; }
    if (!draft.__rationaleDirty) { draft.rationale = suggested.rationale; }
    draft.__suggestedMetadata = suggested;
  }

  /* ============================================================================
     「常用篩選條件」分頁：十張可複選的卡片（裁定 2026-11-03-05）。情境範例在右側條件組合上方，套用後替換整份條件。
     卡片由 FILTER_COMMON_CONDITIONS 一份資料驅動。已選／未選由身分標記得出（isCommonSelectedActive：
     作用中組是否有帶該卡標記 __commonCard 的 rule），不做結構簽章猜測。
     前置條件不足的卡片（依規則需求表，含 GL 沒有配對的欄位）：停用並寫原因，不綁 toggle。
     ============================================================================ */
  function commonCardsHtml(draft) {
    var unavailable = [];
    var cells = Ui.FILTER_COMMON_CONDITIONS.map(function (item) {
      // 暫不提供的卡片優先；其次依卡片要加入的條件查規則需求表。
      var requirement = item.disabled ? '' : requirementNote(commonRuleSpecs(item));
      var disabled = item.disabled || !!requirement;
      var note = item.disabled ? item.note : requirement;

      var state = disabled ? 'none' : commonCardState(draft, item);
      var cls = 'picker-card picker-card--common' +
        (state === 'selected' ? ' picker-card--selected' : '') +
        (state === 'elsewhere' ? ' picker-card--elsewhere' : '') +
        (disabled ? ' picker-card--disabled' : '');
      var pressed = state === 'selected' ? 'true' : 'false';

      // 卡片只寫名稱、停用原因與「也在其他組」。固定資產與費用組合、收入對方科目兩張卡的說明在加入後的條件列寫一次
      //（commonDescriptionHtml）；原本卡片上也寫同一句，同一個畫面出現兩次，所以拿掉卡片上的（裁定 2026-10-29-09）。
      var card = '<button type="button" class="' + cls + '"' +
          ' data-common-card="' + item.key + '"' +
          ' aria-pressed="' + pressed + '"' +
          (disabled ? ' disabled aria-disabled="true"' : '') + '>' +
        '<span class="picker-card__label" title="' + Ui.esc(item.label) + '">' + Ui.esc(item.label) + '</span>' +
        (note ? '<span class="picker-card__note">' + Ui.esc(note) + '</span>' : '') +
        (state === 'elsewhere' ? '<span class="picker-card__elsewhere">也在其他組</span>' : '') +
      '</button>';
      if (disabled) { unavailable.push(card); return ''; }
      return card;
    }).join('');

    return '<div class="filter-source-block" data-common-cards>' +
        '<div class="condition-picker__grid condition-picker__grid--common">' + cells + '</div>' +
        (unavailable.length ? '<details class="filter-unavailable"' + disclosureAttributes('unavailable-common') +
          '><summary>尚不可用（' + unavailable.length + '）</summary><div class="condition-picker__grid">' + unavailable.join('') + '</div></details>' : '') +
      '</div>';
  }

  // 常用篩選條件分頁只放十張卡片；情境範例移到右側條件組合上方的「從情境範例開始」（裁定 2026-11-03-05，取代 2026-10-29-10）。
  function commonPickerHtml(draft) {
    return '<section class="condition-picker condition-picker--common">' + commonCardsHtml(draft) + '</section>';
  }

  // 欄位條件分頁：依欄位列出，常用查法收在「加入」鍵打開的選單裡（裁定 2026-11-03-06、2026-11-03-37）。分頁在程式裡的識別是 field（裁定 2026-11-03-21）。
  function fieldPickerHtml() {
    return '<section class="condition-picker condition-picker--field">' + fieldListHtml() + '</section>';
  }

  // 審計員習慣直接套用預篩選條件（裁定 2026-10-16-09），原本要先加入「沿用第四步預篩選規則」，再到條件卡片的下拉改選，
  // 路徑太長。預篩選條件改成條件總覽的一個分頁，條列每一條規則，按「加入」就帶入那一條；每列是名稱加「加入」的清單列。
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
      '<ul class="filter-check-group__items filter-prescreen-list">' + items + '</ul></section>';
  }

  function addPrescreenCondition(key) {
    if (prescreenRequirementNote(key)) { return; }
    addConditions([conditionFromSpec({ type: 'prescreen', prescreenKey: key })]);
  }

  // 欄位條件分頁依資料型別分成日期、數字、文字與類別四組（裁定 2026-11-03-06、2026-11-03-18），四組寫在同一張白紙上，用小標分開。
  // 列和預篩選分頁的清單同一種寫法：一列一個欄位，名稱在左、「加入」在右，加入一條比較方式待選的條件。
  // 有常用查法的欄位，名稱前有箭頭，按名稱打開玻璃選單列出常用查法；沒有的列在箭頭的位置留同樣寬的空位（裁定 2026-11-03-32）。
  // 原本四組各是一張白卡，常用查法排在列的下一行、鍵和「加入」一樣大，使用者認為仍然雜亂（裁定 2026-11-03-26、2026-11-03-28）。
  // 核心欄位全部列出；GL 沒有配對的照樣列出但不能按，分頁最後寫出是哪些欄位以及回第三步欄位配對補上（裁定 2026-10-22-05）。
  // 攸關資料元素欄位依型別接在核心欄位後面。
  var FIELD_GROUPS = [
    { type: 'date', label: '日期', core: ['glDate', 'approvalDate'] },
    { type: 'amount', label: '數字', core: ['amount'] },
    { type: 'text', label: '文字', core: ['description', 'accountNumber', 'accountName', 'createdBy', 'approvedBy', 'voucherNumber', 'sourceModule', 'lineItem'] },
    { type: 'choice', label: '類別', core: ['drCr', 'manualAuto', 'accountCategory'] }
  ];

  // 類別這一組的三個入口不是 GL 欄位，各自帶入一條既有型別的條件：借貸別預設借方、人工/自動分錄預設人工、科目分類不預選分類
  //（確認頁寫明照 2026-10-14-08 一起做：科目分類只加入一條條件，分類留空待選）。能不能按照各自條件的前置條件。
  // 人工/自動分錄和第三步的欄位同名（裁定 2026-11-03-22）；借貸別與人工/自動分錄底下不放常用查法（裁定 2026-11-03-20）。
  var CHOICE_FIELDS = [
    { id: 'drCr', label: '借貸別', spec: { type: 'drCrOnly', drCr: 'debit' } },
    { id: 'manualAuto', label: '人工/自動分錄', spec: { type: 'manualAuto', isManual: 'true' } },
    { id: 'accountCategory', label: '科目分類', spec: { type: 'accountSide', categoryMode: 'is', categoryIds: [], categorySelection: 'subtree' } }
  ];

  function choiceFields() {
    return CHOICE_FIELDS.map(function (item) {
      return { id: item.id, label: item.label, type: 'choice', spec: item.spec, mapped: !requirementNote([item.spec]) };
    });
  }

  function fieldShortcuts(fieldId) {
    var group = Ui.FIELD_SHORTCUT_GROUPS.find(function (item) { return item.field === fieldId; });
    return group ? group.checks : [];
  }

  // 情境範例選欄位時的欄位按鈕：按鈕文字只寫欄位名稱，和其他欄位同名時才加來源欄名；完整說明放在 title 與 aria-label。
  // attributes 是按鈕的識別屬性（屬性名稱寫到等號前），值是欄位識別；noteId 有值時按鈕停用，並連到說明原因的那一段。
  function fieldButtonHtml(field, fields, attributes, purpose, noteId) {
    var duplicate = fields.filter(function (item) { return item.label === field.label; }).length > 1;
    var text = field.label + (duplicate && field.sourceColumn ? '（' + field.sourceColumn + '）' : '');
    var full = field.extra ? Ui.rdeFieldOptionLabel(field) : field.label;
    return '<button type="button" class="filter-chip' + (field.extra ? ' filter-chip--extra' : '') + '" ' + attributes + '="' + Ui.esc(field.id) + '"' +
      ' title="' + Ui.esc(full) + '" aria-label="' + Ui.esc(purpose + full) + '"' + (noteId ? ' disabled aria-describedby="' + noteId + '"' : '') + '>' +
      Ui.esc(text) + '</button>';
  }

  var FIELD_NOTE_ID = 'filter-field-note';

  // 有常用查法的欄位，「加入」鍵打開選單（裁定 2026-11-03-37）：選單用共用的 .jet-menu 與 Ui.bindMenus（裁定 2026-10-19-08），
  // 和其他選單一樣從鍵的下方長出來。第一項加入這個欄位的一般條件，寫欄位名稱，和沒有選單的「加入」帶入同一條；
  // 下面是常用查法，每一項寫短名稱，按下只加入那一條，完整名稱放在滑鼠提示與報讀名稱。
  // 欄位本身不能用時，鍵與每一項都停用，原因寫在分頁最後那一段，不在每一項下面重複；
  // 欄位能用但查法另有前置條件時，原因寫在選單裡那一項的下一行。
  // 2026-11-03 第三個停點原本是按名稱打開選單、箭頭在名稱前面（裁定 2026-11-03-32），使用者第二次驗收認為整體不夠協調，
  // 在內建瀏覽器試用兩種開法後選了這一種。
  function fieldShortcutsHtml(field, name, full, fieldUnavailable) {
    var menuId = 'filter-field-menu-' + field.id;
    var label = '加入' + field.label + '的條件';
    var fieldDisabled = fieldUnavailable ? ' disabled aria-describedby="' + FIELD_NOTE_ID + '"' : '';
    var items = fieldShortcuts(field.id).map(function (check) {
      var note = fieldUnavailable ? '' : requirementNote(check.conditions);
      var noteId = 'filter-check-note-' + check.key;
      var disabled = fieldUnavailable ? fieldDisabled : (note ? ' disabled aria-describedby="' + noteId + '"' : '');
      return '<button type="button" class="jet-menu__item" role="menuitem" tabindex="-1" data-action="add-rule" data-add-shortcut="' + check.key + '"' +
        ' title="' + Ui.esc(check.name) + '" aria-label="' + Ui.esc('加入「' + check.name + '」') + '"' + disabled + '>' + Ui.esc(check.label) + '</button>' +
        (note ? '<p class="filter-check__note" id="' + noteId + '">' + Ui.esc(note) + '</p>' : '');
    }).join('');
    return '<span class="jet-menu">' +
      '<button type="button" class="btn btn--ghost btn--tiny btn--add jet-menu__toggle" aria-haspopup="menu" aria-expanded="false" aria-controls="' + menuId + '"' +
        ' aria-label="' + Ui.esc(label) + '"' + fieldDisabled + '>加入</button>' +
      '<div class="jet-menu__items filter-field__shortcuts" id="' + menuId + '" role="menu" aria-label="' + Ui.esc(label) + '" hidden>' +
        '<button type="button" class="jet-menu__item" role="menuitem" tabindex="-1" data-action="add-rule" data-add-field="' + Ui.esc(field.id) + '"' +
          ' title="' + Ui.esc(full) + '" aria-label="' + Ui.esc('加入條件：' + full) + '"' + fieldDisabled + '>' + Ui.esc(name) + '</button>' +
        items +
      '</div>' +
    '</span>';
  }

  // 一列一個欄位。名稱和其他欄位同名時加上來源欄名；攸關資料元素的完整名稱放在滑鼠提示。
  // 名稱只是文字；右邊一把「加入」，有常用查法的欄位改成打開選單的「加入」。
  function fieldRowHtml(field, fields, unmapped) {
    var duplicate = fields.filter(function (item) { return item.label === field.label; }).length > 1;
    var name = field.label + (duplicate && field.sourceColumn ? '（' + field.sourceColumn + '）' : '');
    var full = field.extra ? Ui.rdeFieldOptionLabel(field) : field.label;
    if (!field.mapped) { unmapped.push(field.spec || { type: 'fieldValue', field: field.id }); }
    var add = fieldShortcuts(field.id).length ? fieldShortcutsHtml(field, name, full, !field.mapped) :
      '<button type="button" class="btn btn--ghost btn--tiny btn--add" data-action="add-rule" data-add-field="' + Ui.esc(field.id) + '"' +
        ' aria-label="' + Ui.esc('加入條件：' + full) + '"' + (field.mapped ? '' : ' disabled aria-describedby="' + FIELD_NOTE_ID + '"') + '>加入</button>';
    return '<li class="filter-field' + (field.mapped ? '' : ' filter-field--unavailable') + '">' +
      '<div class="filter-field__head"><span class="filter-field__name" title="' + Ui.esc(full) + '">' + Ui.esc(name) + '</span>' + add + '</div></li>';
  }

  function fieldListHtml() {
    var fields = Ui.FilterValues.fields(Store.getState()).concat(choiceFields());
    var unmapped = [];
    var groups = FIELD_GROUPS.map(function (group) {
      var core = group.core.map(function (id) { return fields.find(function (field) { return !field.extra && field.id === id; }); }).filter(Boolean);
      var extra = fields.filter(function (field) { return field.extra && field.type === group.type; });
      var titleId = 'filter-field-group-' + group.type;
      return '<section class="filter-field-group" aria-labelledby="' + titleId + '"><h4 class="filter-field-group__title" id="' + titleId + '">' + group.label + '</h4>' +
        '<ul class="filter-field-list">' + core.concat(extra).map(function (field) { return fieldRowHtml(field, fields, unmapped); }).join('') + '</ul></section>';
    }).join('');
    var note = unmapped.length ? '<p class="filter-check__note filter-field-note" id="' + FIELD_NOTE_ID + '">' + Ui.esc(requirementNote(unmapped)) + '</p>' : '';
    return '<div class="filter-field-groups">' + groups + '</div>' + note;
  }

  // 欄位的「加入」帶入的條件：比較方式仍在右側條件卡片上選。人員與科目編號預設完整值清單；類別那一組照各自的規格帶入。
  function fieldConditionRule(fieldId) {
    var choice = CHOICE_FIELDS.find(function (item) { return item.id === fieldId; });
    if (choice) { return conditionFromSpec(choice.spec); }
    var field = Ui.FilterValues.fields(Store.getState()).find(function (item) { return item.id === fieldId; });
    if (!field) { return null; }
    var rule = Ui.FilterValues.create(field.type);
    if (['createdBy', 'approvedBy', 'accountNumber'].indexOf(field.id) >= 0) { rule.operator = 'in'; }
    delete rule.field; delete rule.fieldId;
    rule[field.extra ? 'fieldId' : 'field'] = field.id;
    if (field.extra && field.type === 'amount') { rule.amountBasis = 'signed'; }
    return rule;
  }

  function findFieldShortcut(key) {
    var found = null;
    Ui.FIELD_SHORTCUT_GROUPS.forEach(function (group) {
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

  // 把一條規格做成可編輯的條件：欄位條件以該欄位型別的預設值為底，其他型別以 newFilterCondition 為底，再套上規格的值。
  function conditionFromSpec(spec) {
    var copy = JSON.parse(JSON.stringify(spec));
    var field = copy.type === 'fieldValue' ? Ui.FilterValues.field(copy, Store.getState()) : null;
    var rule = copy.type === 'fieldValue' ? Ui.FilterValues.create(field ? field.type : 'text') : Ui.newFilterCondition(copy.type);
    Object.keys(copy).forEach(function (key) { rule[key] = copy[key]; });
    if (Array.isArray(copy.conditions)) { rule.conditions = copy.conditions.map(conditionFromSpec); }
    if (rule.type === 'prescreen' && !copy.prescreenKey) { rule.prescreenKey = firstUsablePrescreenKey(); }
    if (rule.__periodEndDays) {
      var range = periodEndRange(rule.__periodEndDays);
      rule.from = range.from; rule.to = range.to;
      delete rule.__periodEndDays;
    }
    return rule;
  }

  // 新條件加入目前作用中的組，沒有條件組就先建立；連接沿用組內現有的 AND 或 OR，空的組用 AND。
  function addRulesToActiveGroup(draft, rules) {
    var target = activeEditableGroup(draft);
    if (!target) {
      target = { join: 'AND', matchScope: 'row', conditions: [] };
      if (draft.groups.length === 0) { keepMotionKey(EMPTY_FIRST_GROUP, target); }
      draft.groups.push(target);
    }
    setActiveGroup(draft, target);
    var join = target.conditions.length ? groupCombinator(target) : 'AND';
    rules.forEach(function (rule) { rule.join = join; target.conditions.push(rule); });
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

  function addFieldShortcut(key) {
    var check = findFieldShortcut(key);
    if (!check || requirementNote(check.conditions)) { return; }
    addConditions(check.conditions.map(conditionFromSpec), !!check.focusValue);
  }

  function disclosureAttributes(key) {
    return ' data-filter-disclosure="' + key + '"' + (FilterStep.viewState.disclosures[key] ? ' open' : '');
  }

  // 開合記在同一份 viewState.disclosures，但有兩種壽命：條件列裡的「查找說明」跟著草稿，儲存、取消編輯或移除編輯中的情境時收起；
  // 條件總覽與「從情境範例開始」托盤跟著這次開案，換草稿不收（裁定 2026-11-03-19），換案件時整份 viewState 重建才收。
  var CASE_DISCLOSURES = ['templates', 'unavailable-common'];
  function clearDraftDisclosures() {
    var kept = {};
    CASE_DISCLOSURES.forEach(function (key) {
      if (FilterStep.viewState.disclosures[key]) { kept[key] = true; }
    });
    FilterStep.viewState.disclosures = kept;
  }

  // 建議的名稱與動機只描述已選條件，不自行編出風險判斷；沒有條件時兩欄留空。名稱列出前兩項條件，超過兩項加上總數，
  // 卡片帶入的條件用卡片名稱。卡片字母已拿掉（裁定 2026-10-29-11），名稱和自訂條件同一種寫法。
  // 有卡片時動機用 commonScenarioRationale；只有自訂條件時把條件念一次。
  function automaticMetadata(draft) {
    var rules = draft.groups.reduce(function (all, group) { return all.concat(group.conditions); }, []);
    if (!rules.length) { return { name: '', rationale: '' }; }
    function shortLabel(rule) {
      var card = cardOfRule(rule);
      if (card) { return card.label; }
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
    if (hasCommonMarker(draft)) { return { name: name, rationale: commonScenarioRationale(draft) }; }
    var expression = readBackPlainText(readBackExpressionHtml(draft, true));
    var rationale = expression ? Ui.GENERATED_RATIONALE_PREFIXES[0] + expression + '。' : '';
    if (Array.from(rationale).length > 180) {
      var description = topics.join('；');
      if (Array.from(description).length > 130) { description = topics.slice(0, 2).join('；') + '等' + rules.length + '項條件'; }
      rationale = Ui.GENERATED_RATIONALE_PREFIXES[1] + description + '。';
    }
    return { name: name, rationale: rationale };
  }

  // 值編輯與重繪後把自動名稱與動機同步到草稿和畫面；手寫或範例帶入的文字由 refreshAutomaticMetadata 保留。
  function syncSuggestedMetadata(container) {
    var draft = Store.getState().filter.draft;
    // 正常路徑已在改條件時收斂；這裡只是保險。卡片身分改變時排一次重繪，讓卡片勾選與條件一致；
    // 不在綁定事件途中同步重繪。
    if (reconcileCommonIdentity(draft)) {
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

  // 情境範例：清單只列名稱，動機放在 title。前置條件不足時停用並寫一句原因。
  function textRdeFields() {
    return Ui.FilterValues.fields(Store.getState()).filter(function (field) { return field.extra && field.type === 'text'; });
  }

  function templateRequirementNote(template) {
    var note = requirementNote(template.groups.reduce(function (rules, group) { return rules.concat(group.conditions); }, []));
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
    return '<p class="filter-example-list__hint">套用後會替換目前的條件。</p><ul class="filter-example-list__items">' + items + '</ul>';
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

  // 選欄位的那一列從範例下面長出來、取消時收回去（裁定 2026-10-13-11）。
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

  // 情境範例列在右側條件組合上方的「從情境範例開始」；Esc 只取消正在選的欄位，草稿不動。
  function bindExampleList(container) {
    var list = container.querySelector('[data-example-list]');
    if (!list) { return; }
    list.addEventListener('keydown', function (event) {
      if (event.key === 'Escape' && FilterStep.viewState.examplePick) { cancelExamplePick(container); }
    });
    bindExampleItems(container);
  }

  // 把範例落地成草稿：規則深拷貝，名稱與動機先當固定文字，條件增減後才改回自動。
  // 指定 commonCard 的範例先用 addCommonToDraft 帶入那張卡，範例第一組的條件接在同一組卡片條件之後
  //（例如「非營業日且排除指定日期」是第 1 組第 1 條非營業日、第 2 條排除日期），其餘組另開；
  // 期末 N 天由查核截止日算起訖。fieldId 空白的條件換成 fieldId（審計員選的文字型攸關資料元素欄位）。
  // 套用後若有條件還要填值，焦點移到第一個要填的欄位，但不代填任何值。
  function applyTemplate(key, fieldId) {
    var template = Ui.FILTER_SCENARIO_TEMPLATES.filter(function (item) { return item.key === key; })[0];
    if (!template) { return; }
    var draft = { name: template.label, rationale: template.rationale, groups: [], __nameDirty: true, __rationaleDirty: true,
      __templateName: true, __templateRationale: true };
    draft.name = uniqueAutomaticName(draft, draft.name);
    if (template.commonCard) {
      var card = Ui.FILTER_COMMON_CONDITIONS.filter(function (item) { return item.key === template.commonCard; })[0];
      if (card) { addCommonToDraft(draft, card); }
    }
    function prepare(rule) {
      if (rule.__periodEndDays) {
        var range = periodEndRange(rule.__periodEndDays);
        rule.from = range.from; rule.to = range.to;
      }
      delete rule.__periodEndDays;
      if (rule.type === 'fieldValue' && rule.fieldId === '' && fieldId) { rule.fieldId = fieldId; delete rule.field; }
      if (Array.isArray(rule.conditions)) { rule.conditions.forEach(prepare); }
      return rule;
    }
    var cardGroup = template.commonCard ? activeEditableGroup(draft) : null;
    template.groups.forEach(function (group, index) {
      var copy = { join: group.join || 'AND', matchScope: group.matchScope || 'row', conditions: group.conditions.map(function (rule) {
        return prepare(JSON.parse(JSON.stringify(rule)));
      }) };
      // 併進卡片那一組只在語意不變時做：範例這一組原本以「且」接在卡片那一組之後，組內只有一條或全部以「且」連接。
      // 組內有「或」時併進去會把「卡片條件 且 (a 或 b)」變成「(卡片條件 且 a) 或 b」，所以照原樣另開一組。
      var sameMeaning = (copy.join || 'AND') === 'AND' && (copy.conditions.length === 1 || groupCombinator(copy) === 'AND');
      if (index === 0 && cardGroup && sameMeaning) {
        copy.conditions.forEach(function (rule, ri) {
          if (ri === 0) { rule.join = cardGroup.conditions.length ? groupCombinator(cardGroup) : 'AND'; }
          cardGroup.conditions.push(rule);
        });
        setActiveGroup(draft, cardGroup);
        return;
      }
      draft.groups.push(copy);
      setActiveGroup(draft, copy);
    });
    FilterStep.viewState.lastSavedName = null;
    FilterStep.viewState.examplePick = null;
    // 畫面上馬上看得到結果，不另記訊息（裁定 2026-10-13-28）。
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

  // 條件來源：欄位條件分頁、「預篩選」分頁、子條件組的加入按鈕、情境範例與常用篩選條件的卡片（原本 bind 裡的兩段）。
  function bindConditionSources(container) {
    // 欄位條件：欄位的「加入」，以及「加入」選單裡的一般條件與常用查法，都加入目前作用中的組。
    container.querySelectorAll('[data-add-field]').forEach(function (button) {
      button.addEventListener('click', function () { addFieldCondition(button.getAttribute('data-add-field')); });
    });
    container.querySelectorAll('[data-add-shortcut]').forEach(function (button) {
      button.addEventListener('click', function () { addFieldShortcut(button.getAttribute('data-add-shortcut')); });
    });
    container.querySelectorAll('[data-add-prescreen]').forEach(function (button) {
      button.addEventListener('click', function () { addPrescreenCondition(button.getAttribute('data-add-prescreen')); });
    });
    // 在這一組加入子條件組或整張傳票條件；新草稿畫面上的空白第 1 組在這時才建立。
    container.querySelectorAll('[data-add-compound]').forEach(function (button) {
      button.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var group = draft.groups[Number(button.getAttribute('data-gi'))];
        if (!group) {
          group = { join: 'AND', matchScope: 'row', conditions: [] };
          if (draft.groups.length === 0) { keepMotionKey(EMPTY_FIRST_GROUP, group); }
          draft.groups.push(group);
        }
        setActiveGroup(draft, group);
        addConditions([Ui.newFilterCondition(button.getAttribute('data-add-compound'))]);
      });
    });
    bindExampleList(container);
    // 常用篩選條件的卡片可複選 toggle —— 已選則移除、未選則加入；累積成同一情境。
    // 停用卡（前置資料不足，例如科目配對未匯入）不綁事件。加入/移除/已選偵測全收斂於上方少數深函式。
    // 畫面上馬上看得到結果，不另記加入或移除的訊息（裁定 2026-10-13-28）。
    container.querySelectorAll('.picker-card--common').forEach(function (btn) {
      if (btn.disabled) { return; }
      btn.addEventListener('click', function () {
        var item = Ui.FILTER_COMMON_CONDITIONS.filter(function (k) {
          return k.key === btn.getAttribute('data-common-card');
        })[0];
        if (!item) { return; }
        var draft = Store.getState().filter.draft;
        if (isCommonSelectedActive(draft, item)) {
          // 取消：只從「作用中組」移除（同訊號在別組不動）。
          removeCommonFromActiveGroup(draft, item);
          refreshAutomaticMetadata(draft, true);
          Store.setFilterDraft(draft);
        } else {
          addCommonToDraft(draft, item); // 每張卡（含非營業日的子條件組）都加入作用中組
          var targetGroup = activeEditableGroup(draft);
          var addedRule = targetGroup && targetGroup.conditions.find(function (rule) { return rule[COMMON_CARD_KEY] === item.key; });
          if (addedRule) { revealAddedRule(addedRule); }
          refreshAutomaticMetadata(draft, true);
          Store.setFilterDraft(draft);
        }
      });
    });

  }

  // 掛到共用的 FilterStep，供其他檔案與測試取用。
  FilterStep.isPatternMenuCommonType = isPatternMenuCommonType;
  FilterStep.availableCommonTypes = availableCommonTypes;
  FilterStep.UNMAPPED_OPTION_SUFFIX = UNMAPPED_OPTION_SUFFIX;
  FilterStep.rdeFieldOptions = rdeFieldOptions;
  FilterStep.rdeFieldValueType = rdeFieldValueType;
  FilterStep.availablePrescreenKeys = availablePrescreenKeys;
  FilterStep.RETIRED_TYPE_NOTE = RETIRED_TYPE_NOTE;
  FilterStep.prescreenRequirementNote = prescreenRequirementNote;
  FilterStep.COMMON_CARD_KEY = COMMON_CARD_KEY;
  FilterStep.COMMON_SOURCE = COMMON_SOURCE;
  FilterStep.hasCommonMarker = hasCommonMarker;
  FilterStep.scenarioSource = scenarioSource;
  FilterStep.commonRuleSpecs = commonRuleSpecs;
  FilterStep.materializeRule = materializeRule;
  FilterStep.isCommonSelected = isCommonSelected;
  FilterStep.isCommonSelectedActive = isCommonSelectedActive;
  FilterStep.isCommonUsedElsewhere = isCommonUsedElsewhere;
  FilterStep.commonCardState = commonCardState;
  FilterStep.addCommonToDraft = addCommonToDraft;
  FilterStep.removeCommonFromActiveGroup = removeCommonFromActiveGroup;
  FilterStep.activeEditableGroup = activeEditableGroup;
  FilterStep.setActiveGroup = setActiveGroup;
  FilterStep.cardOfRule = cardOfRule;
  FilterStep.commonScenarioRationale = commonScenarioRationale;
  FilterStep.COMMON_PAIR_TYPES = COMMON_PAIR_TYPES;
  FilterStep.sameCommonRuleKind = sameCommonRuleKind;
  FilterStep.reconcileCommonIdentity = reconcileCommonIdentity;
  FilterStep.commonRuleKinds = commonRuleKinds;
  FilterStep.uniqueAutomaticName = uniqueAutomaticName;
  FilterStep.releaseTemplateMetadata = releaseTemplateMetadata;
  FilterStep.refreshAutomaticMetadata = refreshAutomaticMetadata;
  FilterStep.commonCardsHtml = commonCardsHtml;
  FilterStep.commonPickerHtml = commonPickerHtml;
  FilterStep.fieldPickerHtml = fieldPickerHtml;
  FilterStep.prescreenPickerHtml = prescreenPickerHtml;
  FilterStep.addPrescreenCondition = addPrescreenCondition;
  FilterStep.FIELD_GROUPS = FIELD_GROUPS;
  FilterStep.fieldButtonHtml = fieldButtonHtml;
  FilterStep.FIELD_NOTE_ID = FIELD_NOTE_ID;
  FilterStep.fieldListHtml = fieldListHtml;
  FilterStep.fieldRowHtml = fieldRowHtml;
  FilterStep.choiceFields = choiceFields;
  FilterStep.fieldShortcuts = fieldShortcuts;
  FilterStep.fieldShortcutsHtml = fieldShortcutsHtml;
  FilterStep.CHOICE_FIELDS = CHOICE_FIELDS;
  FilterStep.exampleListHtml = exampleListHtml;
  FilterStep.fieldConditionRule = fieldConditionRule;
  FilterStep.findFieldShortcut = findFieldShortcut;
  FilterStep.firstUsablePrescreenKey = firstUsablePrescreenKey;
  FilterStep.periodEndRange = periodEndRange;
  FilterStep.conditionFromSpec = conditionFromSpec;
  FilterStep.addRulesToActiveGroup = addRulesToActiveGroup;
  FilterStep.addConditions = addConditions;
  FilterStep.addFieldCondition = addFieldCondition;
  FilterStep.addFieldShortcut = addFieldShortcut;
  FilterStep.disclosureAttributes = disclosureAttributes;
  FilterStep.clearDraftDisclosures = clearDraftDisclosures;
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
