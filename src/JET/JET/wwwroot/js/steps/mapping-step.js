/*
  Step 2：欄位配對。
  來源欄位 → JET 邏輯欄位的對應；審計員逐項手動選取，提交標準化在後端執行。

  每個資料集（GL/TB）的狀態模型（消除「已提交綠字 + 仍可按確認」的語意衝突）：
    未匯入        → 警示（請先匯入資料）
    草稿          → 編輯表格 + 預覽/確認配對
    已提交        → 收合摘要卡（模式、標準化列數、提交時間、key→欄名清單）；
                    只有「重新配對」「預覽標準化資料」兩個動作，編輯表格不渲染
    草稿偏離      → 編輯表格 + 「配對已修改」提示 + 重新確認配對/取消變更
    來源變更失效  → 草稿 + 「來源資料已變更，原配對已失效」橫幅
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  // 「重新配對」的本地旗標：已提交且草稿未偏離時仍顯示編輯畫面。
  var editing = { gl: false, tb: false };

  // PBC-GL 與 PBC-TB 的預覽快取（每資料集一份，依 batchId 失效）。
  // 形狀：null | { batchId, loading:true } | { batchId, error:true } | { batchId, columns, rows, totalCount }
  var sourceCache = { gl: null, tb: null };
  var sourceResponseGuards = {
    gl: Ui.createLatestResponseGuard(),
    tb: Ui.createLatestResponseGuard()
  };

  // 來源欄值分布快取（mapping.valueProfile）：限目前案件的 GL 匯入，以來源欄為鍵。
  // 形狀：{ loading:true } | { error:true } | { blankCount, distinctCount, values, truncated }
  // 這是後端 set-based 聚合的有界結果；前端不掃描來源列、不自行統計。
  var valueProfileCache = Object.create(null);
  var valueProfileProject = null;
  var valueProfileImport = null;
  var mappingCommitErrors = { gl: null, tb: null };
  var pendingCodeInputs = null;
  var codeComparisonScope = null;
  var pendingCreationDate = null;

  Ui.registerWorkflowReset(function () {
    sourceResponseGuards.gl.invalidate();
    sourceResponseGuards.tb.invalidate();
    editing = { gl: false, tb: false };
    sourceCache = { gl: null, tb: null };
    valueProfileCache = Object.create(null);
    valueProfileProject = null;
    valueProfileImport = null;
    mappingCommitErrors = { gl: null, tb: null };
    pendingCodeInputs = null;
    codeComparisonScope = null;
    pendingCreationDate = null;
  });

  function glOptions() {
    return Store.getState().mapping.gl.options;
  }

  // 尚未按「加入」的文字不是已生效代碼，但也不能因背景摘要重繪而消失。
  // 僅保留在目前案件、匯入和來源欄；更換來源只清掉該欄的輸入，不動其他政策。
  function codeInputDrafts() {
    var state = Store.getState();
    var draft = state.mapping.gl.draft;
    if (!pendingCodeInputs || pendingCodeInputs.project !== state.project ||
        pendingCodeInputs.importInfo !== state.importState.gl) {
      pendingCodeInputs = { project: state.project, importInfo: state.importState.gl };
    }
    if (pendingCodeInputs.manualSource !== draft.manual) {
      pendingCodeInputs.manualSource = draft.manual;
      pendingCodeInputs.manualEditor = null;
      pendingCodeInputs.manual = '';
      pendingCodeInputs.automatic = '';
    }
    if (pendingCodeInputs.postingSource !== draft.postingStatus) {
      pendingCodeInputs.postingSource = draft.postingStatus;
      pendingCodeInputs.posting = '';
    }
    return pendingCodeInputs;
  }

  function valueProfiles(kind) {
    var state = Store.getState();
    var importInfo = state.importState[kind];
    // 值摘要只供 GL 使用。換案件或重新匯入時整份丟棄，舊回應不能復活舊快取。
    if (valueProfileProject !== state.project || valueProfileImport !== importInfo) {
      valueProfileCache = Object.create(null);
      valueProfileProject = state.project;
      valueProfileImport = importInfo;
    }
    return valueProfileCache;
  }

  function comparisonScope() {
    var state = Store.getState(), draft = state.mapping.gl.draft;
    if (!codeComparisonScope || codeComparisonScope.project !== state.project ||
        codeComparisonScope.importInfo !== state.importState.gl || codeComparisonScope.manual !== draft.manual ||
        codeComparisonScope.posting !== draft.postingStatus || codeComparisonScope.dcField !== draft.dcField ||
        codeComparisonScope.amountMode !== state.mapping.gl.amountMode) {
      codeComparisonScope = { project: state.project, importInfo: state.importState.gl,
        manual: draft.manual, posting: draft.postingStatus, dcField: draft.dcField, amountMode: state.mapping.gl.amountMode };
    }
    return codeComparisonScope;
  }

  function policyCodes(column) {
    var draft = Store.getState().mapping.gl.draft, options = glOptions(), values = [];
    if (draft.manual === column) {
      values = values.concat(options.manualAutoPolicy.manualValues, options.manualAutoPolicy.automaticValues);
    }
    if (draft.postingStatus === column && options.postingStatusPolicy) {
      values = values.concat(options.postingStatusPolicy.acceptedValues);
    }
    if (draft.dcField === column && ['side', 'flag'].indexOf(Store.getState().mapping.gl.amountMode) >= 0) {
      [draft.dcDebitCode, draft.dcCreditCode].forEach(function (value) {
        if (typeof value === 'string' && value.length) { values.push(value); }
      });
    }
    return values.filter(function (value, index) { return values.indexOf(value) === index; });
  }

  // 只接受後端回傳的等價組或不透明 key；JS 不自行 trim、大小寫展開或作 Unicode 判定。
  function sameCode(left, right, column) {
    if (left === right) { return true; }
    var profile = valueProfiles('gl')[column];
    return !!profile && (profile.comparisonGroups || []).some(function (group) {
      return group.indexOf(left) >= 0 && group.indexOf(right) >= 0;
    });
  }

  function mergeComparisonGroups(profile, incoming, incomingKeys) {
    var groups = (profile.comparisonGroups || []).slice();
    var additions = (incoming || []).slice();
    if (Array.isArray(incomingKeys)) {
      var keys = profile.comparisonKeys || new Map();
      incomingKeys.forEach(function (item) {
        if (item && typeof item.value === 'string' && (typeof item.key === 'string' || item.key === null)) {
          keys.set(item.value, item.key);
        }
      });
      profile.comparisonKeys = keys;
      var byKey = new Map();
      keys.forEach(function (key, value) {
        if (!byKey.has(key)) { byKey.set(key, []); }
        byKey.get(key).push(value);
      });
      // 同 key 的值即使分屬不同請求也能接成一組；null 是已知的空白 key，不是缺少 metadata。
      byKey.forEach(function (values) { additions.push(values); });
    }
    additions.forEach(function (group) {
      var merged = group.slice();
      groups = groups.filter(function (existing) {
        if (!existing.some(function (value) { return merged.indexOf(value) >= 0; })) { return true; }
        existing.forEach(function (value) { if (merged.indexOf(value) < 0) { merged.push(value); } });
        return false;
      });
      groups.push(merged);
    });
    profile.comparisonGroups = groups;
  }

  function comparisonMetadataHas(data, value) {
    if (Array.isArray(data.comparisonKeys)) {
      return data.comparisonKeys.some(function (item) {
        return item && item.value === value && (typeof item.key === 'string' || item.key === null);
      });
    }
    return Array.isArray(data.comparisonGroups) && data.comparisonGroups.some(function (group) {
      return Array.isArray(group) && group.indexOf(value) >= 0;
    });
  }

  function missingComparisonCodes(profile, column) {
    var known = [].concat.apply([], profile.comparisonGroups || []);
    return policyCodes(column).filter(function (value) { return known.indexOf(value) < 0; });
  }

  function ensureCodeComparisons(column, retry, allowWithoutSource) {
    if (!column) { return; }
    var cache = valueProfiles('gl'), profile = cache[column];
    if (allowWithoutSource && (!profile || profile.error)) {
      // 手動加入代碼只需要文字等價性，不必先讀取帳務來源。此快取不代表已取得來源值概況。
      profile = { metadataOnly: true, sourceReadError: !!(profile && profile.error), values: [],
        comparisonGroups: profile && profile.comparisonGroups || [], comparisonKeys: profile && profile.comparisonKeys };
      cache[column] = profile;
    }
    if (!profile || (profile.loading && !allowWithoutSource) || profile.error) { return; }
    var scope = comparisonScope(), missing = missingComparisonCodes(profile, column);
    if (!missing.length) { return; }
    var key = JSON.stringify(policyCodes(column));
    if (profile.comparing && profile.comparing.scope === scope) { return; }
    if (!retry && profile.comparisonError && profile.comparisonError.key === key && profile.comparisonError.scope === scope) { return; }
    var request = { scope: scope };
    profile.comparing = request;
    profile.comparisonError = null;
    // 畫面最多 50 個來源值，其餘政策分批補比較。每次最多 100 個文字 metadata，
    // 不重查母體，也不把傳輸上限變成可保存代碼數的限制。
    var anchors = (profile.values || []).map(function (item) { return item.value; });
    var values = anchors.concat(missing.slice(0, Math.max(1, 100 - anchors.length)));
    global.JetApi.mappingValueProfile({ dataset: 'gl', sourceColumn: column,
      comparisonOnly: true, comparisonValues: values }).then(function (data) {
      if (comparisonScope() !== scope || valueProfileCache !== cache || cache[column] !== profile || profile.comparing !== request) { return; }
      if (values.some(function (value) { return !comparisonMetadataHas(data, value); })) { throw new Error('缺少來源值對應'); }
      mergeComparisonGroups(profile, data.comparisonGroups, data.comparisonKeys);
      profile.comparing = null;
      Store.touch();
    }).catch(function () {
      if (comparisonScope() !== scope || valueProfileCache !== cache || cache[column] !== profile || profile.comparing !== request) { return; }
      profile.comparing = null;
      profile.comparisonError = { key: key, scope: scope };
      Store.touch();
    });
  }

  function ensureDcCodeComparisons() {
    var mapping = Store.getState().mapping.gl, draft = mapping.draft;
    if (['side', 'flag'].indexOf(mapping.amountMode) >= 0 && draft.dcField && draft.dcDebitCode && draft.dcCreditCode) {
      ensureCodeComparisons(draft.dcField, false, true);
    }
  }

  function comparisonNotice(profile, column) {
    if (!missingComparisonCodes(profile, column).length) { return ''; }
    return profile.comparisonError && profile.comparisonError.scope === comparisonScope()
      ? Ui.loadFailureHtml('來源值對應尚未確認。', { 'data-retry-comparison': column })
      : '<p class="map-options__note" role="status">正在確認來源值對應…</p>';
  }

  function acceptSourceValueCheck(profile, requested, data) {
    if (data.sourceValueCheckStatus === 'unsupportedProvider') {
      profile.sourceValueCheckStatus = 'unsupportedProvider';
      return;
    }
    if (!Array.isArray(data.missingComparisonValues)) { throw new Error('來源值是否存在尚未核對'); }
    profile.sourceValueCheckStatus = 'checked';
    var checked = profile.checkedSourceValues || [];
    profile.checkedSourceValues = checked.concat(requested.filter(function (value) { return checked.indexOf(value) < 0; }));
    profile.missingSourceValues = (profile.missingSourceValues || []).filter(function (value) {
      return requested.indexOf(value) < 0;
    }).concat(data.missingComparisonValues.filter(function (value) { return requested.indexOf(value) >= 0; }));
  }

  function uncheckedSourceCodes(profile, column) {
    return policyCodes(column).filter(function (value) { return (profile.checkedSourceValues || []).indexOf(value) < 0; });
  }

  // 存在性只能由全來源查詢回答；不能把前 50 個顯示值當成完整集合。
  // comparisonOnly 仍只比較文字中繼資料，這裡另以明確的 opt-in 請求查詢來源。
  function ensureSourceCodePresence(column, retry) {
    // 即使暫時取消人工欄也要記住範圍已改變，切回同欄時不能沿用舊請求的等待狀態。
    var scope = comparisonScope();
    var cache = valueProfiles('gl'), profile = cache[column];
    if (!profile || profile.metadataOnly || profile.loading || profile.error || profile.sourceValueCheckStatus === 'unsupportedProvider' ||
        (profile.comparing && profile.comparing.scope === scope) || missingComparisonCodes(profile, column).length) { return; }
    var missing = uncheckedSourceCodes(profile, column);
    if (!missing.length || (profile.sourceChecking && profile.sourceChecking.scope === scope)) { return; }
    var key = JSON.stringify(missing);
    if (!retry && profile.sourceCheckError && profile.sourceCheckError.scope === scope && profile.sourceCheckError.key === key) { return; }
    var request = { scope: scope, values: missing.slice(0, 100) };
    profile.sourceChecking = request; profile.sourceCheckError = null;
    global.JetApi.mappingValueProfile({ dataset: 'gl', sourceColumn: column, limit: Ui.VALUE_PROFILE_LIMIT,
      comparisonValues: request.values, checkSourceValues: true }).then(function (data) {
      if (comparisonScope() !== scope || valueProfileCache !== cache || cache[column] !== profile || profile.sourceChecking !== request) { return; }
      acceptSourceValueCheck(profile, request.values, data);
      mergeComparisonGroups(profile, data.comparisonGroups, data.comparisonKeys);
      profile.sourceChecking = null; Store.touch();
    }).catch(function () {
      if (comparisonScope() !== scope || valueProfileCache !== cache || cache[column] !== profile || profile.sourceChecking !== request) { return; }
      profile.sourceChecking = null; profile.sourceCheckError = { key: key, scope: scope }; Store.touch();
    });
  }

  function sourcePresenceNotice(profile, column) {
    if (!profile || profile.metadataOnly) {
      return '<p class="map-options__note">清單代碼是否出現在來源裡尚未核對。按「讀取來源值」後，才會查詢完整來源。</p>';
    }
    if (profile.loading || profile.error) { return ''; }
    if (profile.sourceValueCheckStatus === 'unsupportedProvider') {
      return '<p class="map-options__note">線上案件無法核對清單代碼是否出現在來源中，仍可確認配對。</p>';
    }
    if (!uncheckedSourceCodes(profile, column).length) { return ''; }
    return profile.sourceCheckError && profile.sourceCheckError.scope === comparisonScope()
      ? Ui.loadFailureHtml('來源值是否存在尚未核對。', { 'data-retry-source-check': column })
      : '<p class="map-options__note" role="status">正在核對清單代碼是否出現在來源裡…</p>';
  }

  function missingSourceCode(value, column) {
    var profile = valueProfiles('gl')[column];
    return !!profile && (profile.missingSourceValues || []).indexOf(value) >= 0;
  }

  // 惰性取得來源欄值分布。只發 mapping.valueProfile，載入完成後 touch() 重繪；
  // 值、次數、空白數與是否截斷全部由後端決定，前端不補算 distinct。
  function ensureValueProfile(kind, column) {
    if (!column) { return; }
    var cache = valueProfiles(kind);
    if (cache[column] && !cache[column].error && !cache[column].metadataOnly) { return; }
    var requestState = Store.getState();
    var project = requestState.project;
    var importInfo = requestState.importState[kind];
    var comparison = comparisonScope();
    var previous = cache[column];
    var entry = { loading: true, comparisonGroups: previous && previous.comparisonGroups || [],
      comparisonKeys: previous && previous.comparisonKeys ? new Map(previous.comparisonKeys) : undefined };
    // 不同欄位可同時完成；同一欄的重試才取代前一次請求。
    function accept() {
      var latest = Store.getState();
      return !!project && latest.project === project && latest.importState[kind] === importInfo
        && valueProfileCache === cache && cache[column] === entry;
    }
    cache[column] = entry;
    Store.touch();
    var comparisonValues = policyCodes(column).slice(0, 100);
    var checkSourceValues = requestState.mapping.gl.draft.manual === column;
    global.JetApi.mappingValueProfile({
      dataset: 'gl',
      sourceColumn: column,
      limit: Ui.VALUE_PROFILE_LIMIT,
      comparisonValues: comparisonValues,
      checkSourceValues: checkSourceValues
    }).then(function (data) {
      if (!accept()) { return; }
      cache[column] = {
        blankCount: data.blankCount,
        distinctCount: data.distinctCount,
        values: data.values || [],
        truncated: !!data.truncated,
        comparisonGroups: comparisonScope() === comparison ? entry.comparisonGroups : [],
        comparisonKeys: comparisonScope() === comparison ? entry.comparisonKeys : undefined
      };
      if (comparisonScope() === comparison) { mergeComparisonGroups(cache[column], data.comparisonGroups, data.comparisonKeys); }
      if (checkSourceValues && comparisonScope() === comparison) {
        var loaded = cache[column];
        // 缺少新欄位時只標為尚未核對，不使已取得的值概況失敗。
        if (Array.isArray(data.missingComparisonValues) || data.sourceValueCheckStatus === 'unsupportedProvider') {
          acceptSourceValueCheck(loaded, comparisonValues, data);
        } else { loaded.sourceCheckError = { key: JSON.stringify(uncheckedSourceCodes(loaded, column)), scope: comparison }; }
      }
      Store.touch();
    }).catch(function () {
      if (!accept()) { return; }
      cache[column] = { error: true, comparisonGroups: entry.comparisonGroups, comparisonKeys: entry.comparisonKeys };
      Store.touch();
    });
  }

  // 兩種編輯介面使用同一欄位集合；字面值與三態日期只改呈現，不另刪欄位。
  function fieldsForMode(fields, mode) {
    return fields.filter(function (f) {
      if (f.req === 'always' || f.req === 'optional') { return true; }
      return Array.isArray(f.req) && f.req.indexOf(mode) >= 0;
    });
  }

  function assignableFields(fields, mode) {
    return fieldsForMode(fields, mode).filter(function (field) { return !field.literal; });
  }

  // 反推：某來源欄目前被指派給哪個 JET 欄位（排除字面值欄，避免字面值剛好等於欄名時誤判）。
  function fieldForColumn(fields, mappingState, column) {
    var literalKeys = {};
    fields.forEach(function (f) { if (f.literal) { literalKeys[f.key] = true; } });
    var draft = mappingState.draft;
    return Object.keys(draft).filter(function (k) {
      return !literalKeys[k] && draft[k] === column;
    })[0] || '';
  }

  // 當前模式適用的字面值欄（GL side/flag 的借、貸方代碼；TB 沒有）。
  function literalFieldFor(fields, mode) {
    return fields.filter(function (f) {
      return f.literal && Array.isArray(f.req) && f.req.indexOf(mode) >= 0;
    });
  }

  // 當前模式下尚未指派的必填欄位（含字面值必填欄）。
  function missingRequired(fields, mode, mappingState) {
    return fields.filter(function (f) {
      return !f.literal && Ui.isRequired(f, mode) && !mappingState.draft[f.key];
    });
  }

  // 切模式：清掉不適用新模式的金額類欄位指派（否則標頭下拉顯示不到的殘留會留在 draft），再設模式。
  // 兩件事一次寫入、只重繪一次，選取與提示的動態才會在按下後的下一格就開始。
  function changeMode(kind, fields, mode) {
    var draft = Store.getState().mapping[kind].draft;
    var pruned = {};
    Object.keys(draft).forEach(function (k) {
      var f = fields.filter(function (x) { return x.key === k; })[0];
      if (!f) { return; }
      if (Array.isArray(f.req) && f.req.indexOf(mode) < 0) { return; }
      pruned[k] = draft[k];
    });
    Store.changeMappingMode(kind, pruned, mode);
  }

  // 對照表與右側預覽使用相同的有界分頁；換頁不改配對草稿。
  function ensureSourcePreview(kind, importInfo, page) {
    if (!importInfo) { return; }
    var c = sourceCache[kind];
    if (page == null && c && c.batchId === importInfo.batchId && (c.loading || c.rows || c.error)) { return; }
    page = page == null ? 1 : page;
    var requestState = Store.getState();
    var projectId = requestState.project ? requestState.project.projectId : null;
    var batchId = importInfo.batchId;
    var acceptResponse = sourceResponseGuards[kind].issue(function () {
      var latest = Store.getState();
      var latestImport = latest.importState[kind];
      return !!latest.project && latest.project.projectId === projectId &&
        !!latestImport && latestImport.batchId === batchId;
    });
    var previous = c && c.batchId === batchId && c.rows ? c : null;
    sourceCache[kind] = Object.assign({}, previous || { batchId: batchId, page: page }, { loading: true, error: false });
    global.JetApi.queryDataPreview({ dataset: kind === 'gl' ? 'pbcGl' : 'pbcTb', limit: Ui.TABLE_PAGE_SIZE, offset: (page - 1) * Ui.TABLE_PAGE_SIZE })
      .then(function (data) {
        if (!acceptResponse()) { return; }
        sourceCache[kind] = {
          batchId: batchId,
          page: page,
          columns: data.columns || [],
          rows: data.rows || [],
          // 後端只在第一頁計算總列數；換頁沿用同一批次已知的總數。
          totalCount: data.totalCount != null ? data.totalCount : (previous ? previous.totalCount : 0)
        };
        Store.touch();
      })
      .catch(function () {
        if (!acceptResponse()) { return; }
        sourceCache[kind] = Object.assign({}, previous || { batchId: batchId, page: page }, { loading: false, error: true, retryPage: page });
        Store.touch();
      });
  }

  function sourcePagerHtml(kind, cache) {
    if (!cache) { return ''; }
    var status = cache.error ? '<span role="alert">預覽載入失敗。</span> <button type="button" class="btn btn--ghost btn--tiny" data-source-retry>重試</button>' : '';
    return (cache.rows ? Ui.pageControlsHtml(cache.page || 1, cache.totalCount, {
      previousAttribute: 'data-source-previous', nextAttribute: 'data-source-next', busy: cache.loading
    }) : '') + status;
  }

  // 配對介面切換（簡易清單／對照表格）。渲染在 GL 與 TB「各自的」編輯區頂部，
  // 讓使用者在任一資料集就地切換、不必捲回頁面最上方。兩處共用同一份 session 級
  // mappingUiMode（bump 後全步重繪，兩處 is-active 一致），只是入口就近可達。
  // 已提交的摘要卡是唯讀、無可切換的編輯介面，故該狀態不渲染此切換。
  // 兩種介面只換畫面的呈現，不改配對草稿，所以用共用的頁籤寫法（Ui.tabAttributes），不是選項按鈕。
  function uiModeToggleHtml(kind) {
    var uiMode = Store.getState().mappingUiMode;
    function btn(value, label) {
      var id = 'mapping-ui-' + kind + '-' + value;
      return '<button type="button" class="seg-toggle__btn" data-ui-mode="' + value + '" data-focus-key="' + id + '"' +
        Ui.tabAttributes(uiMode === value, 'map-layout-' + kind, id) + '>' + label + '</button>';
    }
    return '<div class="seg-toggle" role="tablist" aria-label="配對介面">' +
      btn('classic', '簡易清單') + btn('grid', '對照表格') +
      '</div>';
  }

  function mapLayoutPanelAttributes(kind) {
    return ' id="map-layout-' + kind + '" role="tabpanel" aria-labelledby="mapping-ui-' + kind + '-' + Store.getState().mappingUiMode + '"';
  }

  function bindUiModeToggle(container) {
    // 方向鍵由 ui-core.js 的共用頁籤處理，換到那一個等於按下它。
    Ui.bindTabs(container);
    container.querySelectorAll('[data-ui-mode]').forEach(function (b) {
      b.addEventListener('click', function () {
        Store.setMappingUiMode(b.getAttribute('data-ui-mode'));
      });
    });
  }

  function render(container, state) {
    codeInputDrafts();
    comparisonScope();
    if (!state.project) {
      container.innerHTML = Ui.noProjectPanel('欄位配對');
      Ui.bindNoProjectPanel(container);
      return;
    }

    discardStaleMappingCommitErrors(state);
    container.innerHTML =
      '<div class="panel panel--wide panel--mapping" data-motion-key="mapping-panel">' +
        '<h2 class="panel__title">欄位配對</h2>' +
        Ui.downstreamResetNoticeHtml(state) +
        mappingSection('gl', 'GL 欄位配對', Ui.GL_FIELDS, Ui.GL_MODES,
          state.importState.gl, state.mapping.gl, state.mapping.gl.amountMode) +
        mappingSection('tb', 'TB 欄位配對', Ui.TB_FIELDS, Ui.TB_MODES,
          state.importState.tb, state.mapping.tb, state.mapping.tb.changeMode) +
        Ui.stepFooterHtml(state) +
      '</div>';

    bindUiModeToggle(container);
    bindMappingSection(container, 'gl', Ui.GL_FIELDS);
    bindMappingSection(container, 'tb', Ui.TB_FIELDS);
    Ui.bindStepFooter(container);
  }

  // 簡易清單與對照表格共用同一份提交資格鏡像；後端 mapping.commit 仍是權威驗證。
  // GL 另檢查標準化政策草稿（核准日互斥、過帳狀態政策、人工／自動代碼、攸關資料元素欄位）；
  // 這些只是就近引導，值的正規化、去重與跨欄裁定仍全在系統端。
  function mappingCommitEligibility(fields, mode, mappingState, kind) {
    var required = fields.filter(function (field) { return !field.literal && Ui.isRequired(field, mode); });
    var missing = missingRequired(fields, mode, mappingState);
    var problems = kind === 'gl' ? glOptionProblems(mappingState) : [];
    return {
      required: required,
      missing: missing,
      problems: problems,
      canCommit: missing.length === 0 && problems.length === 0
    };
  }

  // GL 標準化政策的前端引導檢查。每條都對應後端 mapping.commit.gl 的既有規則，
  // 不新增語意：核准日三態互斥、過帳狀態政策至少一個接受值或含空白；人工／自動
  // 可逐值指定或明確選單側補集，兩組代碼不得重複。已勾選的攸關資料元素欄位需要顯示名稱。
  function glOptionProblems(mappingState) {
    var options = glOptions();
    var draft = mappingState.draft;
    var problems = [];

    if (['side', 'flag'].indexOf(mappingState.amountMode) >= 0 &&
        !String(draft.dcDebitCode || '').trim() && !String(draft.dcCreditCode || '').trim()) {
      problems.push('借方代碼或貸方代碼至少填寫一個');
    }
    if (['side', 'flag'].indexOf(mappingState.amountMode) >= 0 && draft.dcDebitCode && draft.dcCreditCode &&
        sameCode(draft.dcDebitCode, draft.dcCreditCode, draft.dcField)) {
      problems.push('借方與貸方代碼必須不同');
    }

    if (options.approvalDateMode === 'mapped' && !draft.docDate) {
      problems.push('傳票核准日請選擇來源欄，或改選「沒有核准日」或「與總帳日期相同」');
    }
    if (options.approvalDateMode === 'sameAsPostDate' && draft.docDate) {
      problems.push('核准日選「與總帳日期相同」時，不可同時指派「傳票核准日」來源欄');
    }
    if (options.approvalDateMode === 'unmapped' && draft.docDate) {
      problems.push('核准日選「沒有核准日」時，不可同時指派「傳票核准日」來源欄');
    }

    if (draft.postingStatus) {
      var policy = options.postingStatusPolicy;
      var accepted = policy && Array.isArray(policy.acceptedValues) ? policy.acceptedValues : [];
      if (accepted.length === 0 && !(policy && policy.includeBlank)) {
        problems.push('已指派過帳狀態來源欄時，必須選至少一個代表「已過帳」的值，或勾選接受空白');
      }
    }

    var manual = options.manualAutoPolicy || { manualValues: [], automaticValues: [] };
    if ((!manual.manualValues.length && manual.unlistedValueKind !== 'manual') ||
        (!manual.automaticValues.length && manual.unlistedValueKind !== 'automatic')) {
      problems.push('人工與自動代碼各需至少一個值；只列一側時，請在「判定方式」選只列人工或只列自動');
    }

    if ((options.rdeFields || []).some(function (field) {
      return !field.label || !field.label.trim();
    })) {
      problems.push('已勾選的攸關資料元素欄位都需要顯示名稱');
    }

    return problems;
  }

  /* ---- GL 標準化政策編輯器 ---------------------------------------------------- */

  function glOptionsHtml(importInfo, mappingState) {
    var options = glOptions();
    var draft = mappingState.draft;
    var dcProfile = draft.dcField && valueProfiles('gl')[draft.dcField];
    var dcNotice = ['side', 'flag'].indexOf(mappingState.amountMode) >= 0 && draft.dcDebitCode && draft.dcCreditCode && dcProfile
      ? comparisonNotice(dcProfile, draft.dcField) : '';
    // 總帳配對設定標上識別：換金額欄位格式時裡面多出或少掉的設定各自長出來或收起來，其他的設定跟著平順移動（app.js 的 JetMotion）。
    return (
      '<section class="map-options" aria-label="總帳配對設定" data-bind="gl-options" data-motion-key="map-options-gl">' +
        dcNotice +
        postingStatusPolicyHtml(options, draft) +
        manualAutoPolicyHtml(options, draft) +
        rdeFieldsHtml(options) +
      '</section>'
    );
  }

  // 傳票核准日只在這一個下拉設定（使用者 2026-10-08 畫面註解）：選來源欄就代表由來源欄提供，
  // 另有「沒有核准日」與「與總帳日期相同」兩個選項。值加前綴，因為來源欄名可能是任意字串；
  // 送出的 approvalDateMode 與 docDate 組合和以前相同，後端契約不變。
  function approvalChoiceHtml(options, draft, importInfo) {
    var mode = options.approvalDateMode;
    var selected = mode === 'mapped' && draft.docDate
      ? 'column:' + draft.docDate
      : 'mode:' + (mode === 'sameAsPostDate' ? 'sameAsPostDate' : 'unmapped');
    function option(value, label) {
      return '<option value="' + Ui.esc(value) + '"' + (value === selected ? ' selected' : '') + '>' + Ui.esc(label) + '</option>';
    }
    var modeLabels = {};
    Ui.GL_APPROVAL_DATE_MODES.forEach(function (m) { modeLabels[m.value] = m.label; });
    var columns = importInfo.columns || [];

    var note = mode === 'mapped' && draft.docDate && draft.docDate === draft.postDate
      ? '這一欄也是總帳日期的來源欄；要讓核准日等於總帳日期，請選「與總帳日期相同」。' : '';

    return '<div class="map-approval" data-mapping-field="docDate">' +
      '<select class="mapping-table__select" data-approval-choice data-mapping-key="docDate"' +
        ' data-focus-key="gl-approval-choice" aria-label="傳票核准日的來源"' + (note ? ' aria-describedby="gl-approval-help"' : '') + '>' +
        option('mode:unmapped', modeLabels.unmapped) +
        option('mode:sameAsPostDate', modeLabels.sameAsPostDate) +
        (columns.length ? '<optgroup label="來源欄">' + columns.map(function (column) {
          return option('column:' + column, column);
        }).join('') + '</optgroup>' : '') +
      '</select>' +
      (note ? '<p class="map-options__note" id="gl-approval-help" data-motion-key="gl-approval-help">' + note + '</p>' : '') +
      '</div>';
  }

  function postingStatusPolicyHtml(options, draft) {
    if (!draft.postingStatus) {
      return '<fieldset class="map-options__group">' +
        '<legend class="map-options__legend">過帳狀態</legend>' +
        '<p class="map-options__note">尚未指派「過帳狀態」來源欄；不指派時不套用狀態排除，' +
          '本次測試只依查核期間選取分錄。</p>' +
        '</fieldset>';
    }

    var policy = options.postingStatusPolicy || { acceptedValues: [], includeBlank: false };
    var accepted = policy.acceptedValues || [];
    var profile = valueProfiles('gl')[draft.postingStatus];
    var body;
    // 讀取中的提示、失敗提示與讀到的值清單各有識別：按「讀取來源值」後清單從標題下長出來，提示收起來，
    // 和其他展開的區塊一樣（app.js 的 JetMotion，使用者 2026-10-13 要求展開與收合一致）。
    // 提示用「state:」、清單用「:欄名」，來源欄剛好叫 loading 或 error 時兩者也不會被當成同一個東西。
    if (!profile || profile.metadataOnly) {
      body = '<button type="button" class="btn btn--ghost btn--tiny" data-action="load-posting-profile">' +
        '讀取來源值</button>';
    } else if (profile.loading) {
      body = '<p class="map-options__note" data-motion-key="posting-values-state:loading">讀取來源值中…</p>';
    } else if (profile.error) {
      body = Ui.loadFailureHtml('讀取來源值失敗。', { 'data-action': 'load-posting-profile' }, { 'data-motion-key': 'posting-values-state:error' });
    } else {
      var postingComparisonReady = missingComparisonCodes(profile, draft.postingStatus).length === 0;
      var checks = profile.values.map(function (item, index) {
        var checked = accepted.some(function (value) {
          return sameCode(value, item.value, draft.postingStatus);
        });
        return '<label class="value-option">' +
          '<input type="checkbox" data-posting-value="' + Ui.esc(item.value) + '"' +
            ' data-focus-key="posting-value-' + Ui.esc(JSON.stringify([draft.postingStatus, item.value])) + '"' +
            (postingComparisonReady ? '' : ' disabled title="正在確認來源值對應"') +
            (checked ? ' checked' : '') + ' id="posting-value-' + index + '">' +
          '<span class="value-option__text">' + Ui.esc(item.value) + '</span>' +
          '<span class="value-option__count">' + Number(item.count).toLocaleString() + ' 列</span>' +
          '</label>';
      }).join('');
      body = '<div class="map-options__values" data-motion-key="' + Ui.esc('posting-values:' + draft.postingStatus) + '">' +
        '<p class="map-options__note">來源欄「' + Ui.esc(draft.postingStatus) + '」共 ' +
          Number(profile.distinctCount).toLocaleString() + ' 種值、空白 ' +
          Number(profile.blankCount).toLocaleString() + ' 列。勾選代表「已過帳」的值。' +
          (profile.truncated ? '值太多，只列出最常出現的幾種；其餘可用下方欄位補上。' : '') +
        '</p>' +
        comparisonNotice(profile, draft.postingStatus) +
        '<div class="value-option-list" aria-busy="' + !postingComparisonReady + '" data-preserve-scroll="posting-values-' + Ui.esc(draft.postingStatus) + '">' + checks + '</div>' +
        (profile.truncated
          ? '<div class="map-options__inline">' +
              '<label class="map-options__inline-label" for="posting-value-add">補充其他值</label>' +
              '<input class="form__input form__input--tiny" type="text" id="posting-value-add"' +
                ' data-bind="posting-value-add" data-focus-key="posting-value-add-' + Ui.esc(draft.postingStatus) +
                '" value="' + Ui.esc(codeInputDrafts().posting || '') + '">' +
              '<button type="button" class="btn btn--ghost btn--tiny" data-action="add-posting-value" aria-label="加入已過帳的值" data-focus-key="add-posting-value-' + Ui.esc(draft.postingStatus) + '">' +
                '加入</button>' +
            '</div>'
          : '') +
        '</div>';
    }

    var selected = accepted.length
      ? '<p class="map-options__note" data-motion-key="accepted-values">目前接受：' +
          accepted.map(function (value) {
            return '<span class="value-chip" data-motion="inline" data-motion-key="' + Ui.esc('accepted-' + value) + '">' + Ui.esc(value) +
              '<button type="button" class="value-chip__remove" data-remove-posting-value="' +
              Ui.esc(value) + '" aria-label="移除 ' + Ui.esc(value) + '">×</button></span>';
          }).join('') + '</p>'
      : '';

    return '<fieldset class="map-options__group">' +
      '<legend class="map-options__legend">過帳狀態</legend>' +
      body +
      selected +
      '<label class="value-option">' +
        '<input type="checkbox" data-option-bind="includeBlank" data-focus-key="gl-include-blank"' +
          (policy.includeBlank ? ' checked' : '') + '>' +
        '<span class="value-option__text">空白也視為已過帳</span>' +
      '</label>' +
      '</fieldset>';
  }

  // 加入與移除的動態用的代碼識別：值沒變的沿用原本的識別；改了值的那一格沿用同一位置原本的識別。
  // 所以清掉中間一格時收起來的就是那一格，其他格不會換值；就地改字也不會被當成移除再加入。
  var codeMotion = { manual: [], automatic: [] };
  var codeMotionSerial = 0;

  function codeMotionKeys(values, group) {
    var previous = codeMotion[group] || [];
    var used = previous.map(function () { return false; });
    var keys = values.map(function (value) {
      for (var i = 0; i < previous.length; i += 1) {
        if (!used[i] && previous[i].value === value) { used[i] = true; return previous[i].key; }
      }
      return null;
    }).map(function (key, index) {
      if (key) { return key; }
      if (index < previous.length && !used[index]) { used[index] = true; return previous[index].key; }
      codeMotionSerial += 1;
      return 'code-' + group + '-' + codeMotionSerial;
    });
    codeMotion[group] = values.map(function (value, index) { return { value: value, key: keys[index] }; });
    return keys;
  }

  // 代碼排成一列，所以加入與移除時左右收放（data-motion="inline"）。
  function codeInputsHtml(values, group) {
    var keys = codeMotionKeys(values, group);
    return values.map(function (value, index) {
      var source = Store.getState().mapping.gl.draft.manual;
      var missing = missingSourceCode(value, source);
      return '<label class="map-options__code-value" data-motion="inline" data-motion-key="' + Ui.esc(keys[index]) + '"><input type="text" class="form__input"' +
        ' data-source-code="' + Ui.esc(value) + '" data-manual-code-edit="' + group + ':' + index +
        '" data-code-group="' + group + '" data-code-index="' + index + '" aria-label="' +
        (group === 'manual' ? '人工' : '自動') + '代碼 ' + (index + 1) +
        '" data-focus-key="manual-code-edit-' + Ui.esc(source) + '-' + group + '-' + index + '" value="' + Ui.esc(value) + '">' +
        (missing ? '<span class="map-options__note">來源裡沒有這個值</span>' : '') + '</label>';
    }).join('');
  }

  function manualAutoPolicyHtml(options, draft) {
    var policy = options.manualAutoPolicy;
    if (!draft.manual) {
      return '<fieldset class="map-options__group">' +
        '<legend class="map-options__legend">人工/自動分錄代碼</legend>' +
        '<p class="map-options__note">尚未指派「人工/自動分錄」來源欄；不指派時不判定人工或自動，' +
          '需要這項的測試會標示為無法執行。</p>' +
        '</fieldset>';
    }

    // 先呈現判定方式與空白處理，再列代碼及按需核對的來源值（使用者 2026-10-09 第 10 則畫面註解）；
    // 只顯示目前判定方式需要的代碼。預設值不寫入政策，空白與分類結果和以前相同。
    var mode = policy.unlistedValueKind || 'reject';
    var blank = policy.blankValueKind || 'reject';
    var modeHint = {
      reject: '每個非空白值都要指定人工或自動。',
      automatic: '只列出人工代碼；其他非空白值一律算自動。',
      manual: '只列出自動代碼；其他非空白值一律算人工。'
    }[mode];
    var modeSelector = '<div class="map-options__field"><label>如何區分人工與自動<select class="form__select" data-manual-mode data-focus-key="manual-mode">' +
      [['reject', '分別指定人工、自動'], ['automatic', '只指定人工代碼'],
        ['manual', '只指定自動代碼']].map(function (item) {
        return '<option value="' + item[0] + '"' + (item[0] === mode ? ' selected' : '') + '>' + item[1] + '</option>';
      }).join('') + '</select></label>' +
      '<p class="map-options__note">' + modeHint + '</p></div>';
    var profile = valueProfiles('gl')[draft.manual];
    var knownBlankCount = profile && !profile.metadataOnly && !profile.loading && !profile.error ? Number(profile.blankCount) : null;
    var blankSelector = '<div class="map-options__field"><label>來源空白時<select class="form__select" data-manual-blank data-focus-key="manual-blank">' +
      [['reject', '先補齊來源資料'], ['manual', '視為人工'], ['automatic', '視為自動'],
        ['unclassified', '不判定人工或自動，保留供其他測試使用']].map(function (item) {
        return '<option value="' + item[0] + '"' + (item[0] === blank ? ' selected' : '') + '>' + item[1] + '</option>';
      }).join('') + '</select></label>' +
      (knownBlankCount > 0 && blank === 'reject'
        ? '<p class="form-notice form-notice--warning">來源有 ' + knownBlankCount.toLocaleString() + ' 列空白；不選處理方式時確認配對會失敗。</p>' : '') + '</div>';
    var sourceReady = !!profile && !profile.metadataOnly && !profile.loading && !profile.error;
    var showCodes = !sourceReady || codeInputDrafts().manualEditor === 'codes';
    var body;
    if (!profile || profile.metadataOnly) {
      body = (profile && profile.sourceReadError
        ? Ui.loadFailureHtml('讀取來源值失敗，仍可修改代碼或重新讀取。', { 'data-action': 'load-manual-profile' })
        : '<p class="map-options__note"><button type="button" class="btn btn--ghost btn--tiny" data-action="load-manual-profile">讀取來源值</button> 尚未核對來源值，可從清單選擇代碼。</p>') +
        (profile ? comparisonNotice(profile, draft.manual) : '');
    } else if (profile.loading) {
      body = '<p class="map-options__note" data-motion-key="manual-values-state:loading">讀取來源值中…</p>';
    } else if (profile.error) {
      body = Ui.loadFailureHtml('讀取來源值失敗。', { 'data-action': 'load-manual-profile' }, { 'data-motion-key': 'manual-values-state:error' });
    } else {
      var comparisonReady = missingComparisonCodes(profile, draft.manual).length === 0;
      var rows = profile.values.map(function (item, index) {
        var isManual = policy.manualValues.some(function (v) {
          return sameCode(v, item.value, draft.manual);
        });
        var isAutomatic = policy.automaticValues.some(function (v) {
          return sameCode(v, item.value, draft.manual);
        });
        var assigned = !comparisonReady ? 'pending' : (isManual && isAutomatic ? 'conflict' : (isManual ? 'manual' : (isAutomatic ? 'automatic' : 'none')));
        function radio(value, label) {
          return '<label class="mode-option mode-option--tiny">' +
            '<input type="radio" name="manual-code-' + index + '" value="' + value + '"' +
              (comparisonReady ? '' : ' disabled title="正在確認來源值對應"') +
              (assigned === value ? ' checked' : '') +
              ' data-manual-assign="' + Ui.esc(item.value) + '"' +
              ' data-focus-key="manual-value-' + Ui.esc(JSON.stringify([draft.manual, item.value, value])) + '">' +
            '<span>' + label + '</span></label>';
        }
        return '<li class="value-assign">' +
          '<span class="value-assign__value">' + Ui.esc(item.value) + '</span>' +
          '<span class="value-assign__count">' + Number(item.count).toLocaleString() + ' 列</span>' +
          // 每個來源值各有一組選項；以來源值命名這一組，報讀器才不會只聽到重複的「人工」。
          '<span class="value-assign__modes" role="' + (mode === 'reject' ? 'radiogroup' : 'group') +
            '" aria-label="來源值「' + Ui.esc(item.value) + '」">' + (assigned === 'conflict' ? '<span class="map-options__note">人工與自動重複</span>' : '') + (mode === 'reject'
            ? radio('manual', '人工') + radio('automatic', '自動') + radio('none', '不歸類')
            : '<label class="mode-option mode-option--tiny"><input type="checkbox" data-manual-include="' + Ui.esc(item.value) + '"' +
              (comparisonReady ? '' : ' disabled title="正在確認來源值對應"') +
              (comparisonReady && (mode === 'automatic' ? isManual : isAutomatic) ? ' checked' : '') +
              ' data-focus-key="manual-include-' + Ui.esc(JSON.stringify([draft.manual, item.value])) + '"><span>' + (mode === 'automatic' ? '列為人工' : '列為自動') + '</span></label>') + '</span>' +
          '</li>';
      }).join('');
      // 來源值清單整塊有識別（T28、T30）：讀到值或從輸入代碼切回來時整塊長出來，切去輸入代碼時整塊收起來。
      body = '<div class="map-options__values" data-motion-key="' + Ui.esc('manual-values:' + draft.manual) + '">' +
        '<p class="map-options__note">來源欄「' + Ui.esc(draft.manual) + '」共 ' +
          Number(profile.distinctCount).toLocaleString() + ' 種值、空白 ' +
          Number(profile.blankCount).toLocaleString() + ' 列。' +
          (profile.truncated ? (mode === 'reject'
            ? '值太多，只列出最常出現的幾種；可以改用只列一側，其餘非空白值依判定方式歸類。'
            : '值太多，只列出最常出現的幾種；沒有列出的值依判定方式歸類。') : '') +
        '</p>' +
        comparisonNotice(profile, draft.manual) +
        sourcePresenceNotice(profile, draft.manual) +
        '<ul class="value-assign-list" aria-busy="' + !comparisonReady + '" data-preserve-scroll="manual-values-' + Ui.esc(draft.manual) + '">' + rows + '</ul>' +
        '</div>';
    }

    // 代碼整塊與人工、自動兩組各有識別：「改從來源值選取／改用輸入代碼」換整塊、換判定方式多出或少掉一組時，
    // 整塊長出來或收起來，不再一下跳過去（使用者 2026-10-13 要求展開與收合一致）。
    var codes =
      '<div class="map-options__codes" data-motion-key="manual-codes">' +
        (mode === 'manual' ? '' : '<div class="map-options__code-group" data-motion-key="code-group-manual">' +
          '<span class="map-options__code-label">人工代碼</span>' +
          codeInputsHtml(policy.manualValues, 'manual') +
          '<label class="visually-hidden" for="manual-code-add">新增人工代碼</label>' +
          '<input class="form__input form__input--tiny" type="text" id="manual-code-add"' +
            ' data-bind="manual-code-add" data-focus-key="manual-code-add-' + Ui.esc(draft.manual) +
            '" value="' + Ui.esc(codeInputDrafts().manual || '') + '" placeholder="新增代碼">' +
          '<button type="button" class="btn btn--ghost btn--tiny" data-action="add-code"' +
            ' data-code-group="manual" aria-label="加入人工代碼" data-focus-key="add-manual-code-' + Ui.esc(draft.manual) + '">加入</button>' +
        '</div>') +
        (mode === 'automatic' ? '' : '<div class="map-options__code-group" data-motion-key="code-group-automatic">' +
          '<span class="map-options__code-label">自動代碼</span>' +
          codeInputsHtml(policy.automaticValues, 'automatic') +
          '<label class="visually-hidden" for="automatic-code-add">新增自動代碼</label>' +
          '<input class="form__input form__input--tiny" type="text" id="automatic-code-add"' +
            ' data-bind="automatic-code-add" data-focus-key="automatic-code-add-' + Ui.esc(draft.manual) +
            '" value="' + Ui.esc(codeInputDrafts().automatic || '') + '" placeholder="新增代碼">' +
          '<button type="button" class="btn btn--ghost btn--tiny" data-action="add-code"' +
            ' data-code-group="automatic" aria-label="加入自動代碼" data-focus-key="add-automatic-code-' + Ui.esc(draft.manual) + '">加入</button>' +
        '</div>') +
      '</div>';
    var switcher = sourceReady ? '<div class="map-options__entry-tools"><button type="button" class="btn btn--ghost btn--tiny" data-action="manual-entry-' +
      (showCodes ? 'values' : 'codes') + '" data-focus-key="manual-entry-switch">' +
      (showCodes ? '改從來源值選取' : '改用輸入代碼') + '</button></div>' : '';
    var sourceStatus = sourceReady && showCodes
      ? comparisonNotice(profile, draft.manual) + sourcePresenceNotice(profile, draft.manual) : body;
    return '<fieldset class="map-options__group">' +
      '<legend class="map-options__legend">人工/自動分錄代碼</legend>' +
      '<div class="map-options__settings">' + modeSelector + blankSelector + '</div>' +
      switcher + (showCodes ? codes : '') +
      '<div class="map-options__source">' + sourceStatus + '</div>' +
      '</fieldset>';
  }

  // 只列出「沒有被核心欄位配對佔用」的來源欄（規則在 Store.availableGlRdeColumns）；勾選後才需要顯示名稱與型別。
  // 型別解析、空白判定與失敗時的整批 rollback 都在系統端，畫面不猜型別。
  function toggleAllRdeFields(selectAll) {
    var state = Store.getState();
    var current = state.mapping.gl.options.rdeFields || [];
    var columns = Store.availableGlRdeColumns();
    var byColumn = Object.create(null);
    current.forEach(function (field) { byColumn[field.sourceColumn] = field; });
    mappingCommitErrors.gl = null;
    Store.patchGlMappingOptions({
      rdeFields: selectAll
        ? columns.map(function (column) {
            return byColumn[column] || { sourceColumn: column, label: column, valueType: 'text' };
          })
        : []
    });
  }

  function selectCreationDateRde(column) {
    pendingCreationDate = null;
    if (Store.availableGlRdeColumns().indexOf(column) < 0) { return; }
    var fields = glOptions().rdeFields || [];
    var occupied = fields.find(function (field) { return field.sourceColumn === column; });
    if (occupied && (occupied.label !== '傳票建立日' || occupied.valueType !== 'date')) {
      pendingCreationDate = { project: Store.getState().project, batch: Store.getState().importState.gl,
        column: column, fields: JSON.stringify(fields), label: occupied.label };
      Store.touch();
      return;
    }
    if (occupied) { return; }
    var existing = fields.find(function (field) { return field.label === '傳票建立日'; });
    var replacement = Object.assign({}, existing || {}, { sourceColumn: column, label: '傳票建立日', valueType: 'date' });
    var next = fields.map(function (field) { return field === existing ? replacement : field; });
    if (!existing) { next.push(replacement); }
    mappingCommitErrors.gl = null;
    Store.patchGlMappingOptions({ rdeFields: next });
  }

  function currentCreationDateRequest() {
    if (pendingCreationDate && (pendingCreationDate.project !== Store.getState().project ||
        pendingCreationDate.batch !== Store.getState().importState.gl ||
        pendingCreationDate.fields !== JSON.stringify(glOptions().rdeFields || []) ||
        Store.availableGlRdeColumns().indexOf(pendingCreationDate.column) < 0)) { pendingCreationDate = null; }
    return pendingCreationDate;
  }

  function creationDateConfirmationHtml() {
    var request = currentCreationDateRequest();
    if (!request) { return ''; }
    var other = (glOptions().rdeFields || []).some(function (field) { return field.label === '傳票建立日'; });
    return '<div role="status" class="map-options__note" data-motion-key="created-date-confirm">這個來源欄目前是「' + Ui.esc(request.label) + '」欄位。' +
      '改成傳票建立日後，這一欄會以日期讀取。' +
      (other ? '另一個「傳票建立日」欄位會保留，請依來源欄確認是否需要。' : '') +
      '<button type="button" class="btn btn--ghost" data-action="confirm-created-date">把它改成傳票建立日</button>' +
      '<button type="button" class="btn btn--ghost" data-action="cancel-created-date">取消</button></div>';
  }

  function rdeFieldsHtml(options) {
    var selected = options.rdeFields || [];
    var columns = Store.availableGlRdeColumns();
    var selectedByColumn = Object.create(null);
    selected.forEach(function (field) { selectedByColumn[field.sourceColumn] = field; });
    var allSelected = columns.length > 0 && columns.every(function (column) {
      return !!selectedByColumn[column];
    });

    var rows = columns.map(function (column, index) {
      var hit = selectedByColumn[column];
      var controls = hit
        ? '<span class="rde-field__controls" data-motion="fade" data-motion-key="' + Ui.esc('rde-controls-' + column) + '">' +
            '<label class="visually-hidden" for="rde-label-' + index + '">' +
              Ui.esc(column) + ' 的顯示名稱</label>' +
            '<input class="form__input form__input--tiny" type="text" id="rde-label-' + index + '"' +
              ' data-rde-label="' + Ui.esc(column) + '" data-focus-key="rde-label-' + Ui.esc(column) +
              '" value="' + Ui.esc(hit.label) +
              '" maxlength="' + Ui.RDE_MAX_LABEL_LENGTH + '">' +
            '<label class="visually-hidden" for="rde-type-' + index + '">' +
              Ui.esc(column) + ' 的資料類型</label>' +
            '<select id="rde-type-' + index + '" data-rde-type="' + Ui.esc(column) +
              '" data-focus-key="rde-type-' + Ui.esc(column) + '">' +
              Ui.RDE_VALUE_TYPES.map(function (t) {
                return '<option value="' + t.value + '"' +
                  (hit.valueType === t.value ? ' selected' : '') + '>' + t.label + '</option>';
              }).join('') +
            '</select>' +
          '</span>'
        : '';
      // 每一列有識別：換金額欄位格式後多出或少掉的來源欄各自長出來或收起來（app.js 的 JetMotion）。
      return '<li class="rde-field' + (hit ? ' is-selected' : '') + '" data-motion-key="' + Ui.esc('rde-field-' + column) + '">' +
        '<label class="value-option">' +
          '<input type="checkbox" data-rde-column="' + Ui.esc(column) +
            '" data-focus-key="rde-column-' + Ui.esc(column) + '"' + (hit ? ' checked' : '') + '>' +
          '<span class="value-option__text">' + Ui.esc(column) + '</span>' +
        '</label>' +
        controls +
        '</li>';
    }).join('');

    return '<fieldset class="map-options__group">' +
      '<legend class="map-options__legend">攸關資料元素欄位</legend>' +
      '<p class="map-options__note map-options__note--wide">勾選要用於篩選、可靠性抽樣及底稿的欄位。' +
        '未勾選的欄位只保留在 PBC-GL。</p>' +
      (columns.length ? '<label>傳票建立日<select data-rde-created-date data-focus-key="rde-created-date"><option value="">選擇建立日期的來源欄</option>' +
        columns.map(function (column) { return '<option value="' + Ui.esc(column) + '"' + (selected.some(function (f) { return f.label === '傳票建立日' && f.sourceColumn === column; }) ? ' selected' : '') + '>' + Ui.esc(column) + '</option>'; }).join('') + '</select></label>' : '') +
      creationDateConfirmationHtml() +
      (rows
        ? '<div class="map-options__inline">' +
            '<button type="button" class="btn btn--ghost btn--tiny" data-action="select-all-rde" data-focus-key="rde-select-all"' +
              (allSelected ? ' disabled' : '') + '>全選</button>' +
            '<button type="button" class="btn btn--ghost btn--tiny" data-action="clear-all-rde" data-focus-key="rde-clear-all"' +
              (selected.length === 0 ? ' disabled' : '') + '>全部取消</button>' +
          '</div><ul class="rde-field-list" data-preserve-scroll="rde-fields">' + rows + '</ul>'
        : '<p class="map-options__note">目前所有來源欄都已對應到 JET 欄位，沒有可額外保留的欄位。</p>') +
      '</fieldset>';
  }

  /* ---- 狀態判定 -------------------------------------------------------------- */

  // 草稿（非空值）與已提交快照逐鍵相等，且模式一致。
  function draftMatchesCommitted(mappingState, mode) {
    var committed = mappingState.committed;
    if (!committed || !committed.mapping) { return false; }
    if (committed.mode && committed.mode !== mode) { return false; }
    if (committed.options && JSON.stringify(mappingState.options) !== JSON.stringify(committed.options)) { return false; }

    var draftKeys = Object.keys(mappingState.draft).filter(function (k) { return mappingState.draft[k]; });
    var committedKeys = Object.keys(committed.mapping).filter(function (k) { return committed.mapping[k]; });
    if (draftKeys.length !== committedKeys.length) { return false; }

    return draftKeys.every(function (k) { return mappingState.draft[k] === committed.mapping[k]; });
  }

  function selectedMode(modes, mode) {
    return modes.filter(function (m) { return m.value === (mode === 'flag' ? 'side' : mode); })[0];
  }

  function modeLabel(modes, mode) {
    var hit = selectedMode(modes, mode);
    return hit ? hit.label : mode;
  }

  // 金額方式的單選群組。選取的方式要相減或依正負號判斷時，下方說明金額怎麼算（使用者 2026-10-21），
  // 報讀器也從群組讀到這句說明；直接取用一欄的方式沒有說明。
  function modeGroupHtml(kind, modes, mode) {
    var radios = modes.map(function (m) {
      return '<label class="mode-option">' +
        '<input type="radio" name="mode-' + kind + '" value="' + m.value + '"' +
          ((mode === 'flag' ? 'side' : mode) === m.value ? ' checked' : '') + ' data-focus-key="mode-' + kind + '-' + m.value + '">' +
        '<span>' + m.label + '</span></label>';
    }).join('');
    var hit = selectedMode(modes, mode);
    var noteId = 'mapping-' + kind + '-mode-note';
    return '<div class="mode-group" role="radiogroup" aria-label="金額欄位格式"' +
        (hit && hit.note ? ' aria-describedby="' + noteId + '"' : '') + '>' + radios + '</div>' +
      (hit && hit.note ? '<p class="mode-group__note" id="' + noteId + '">' + Ui.esc(hit.note) + '</p>' : '');
  }

  /* ---- 渲染 ------------------------------------------------------------------ */

  function mappingSection(kind, title, fields, modes, importInfo, mappingState, mode) {
    if (!importInfo) {
      return (
        '<section class="mapping-section">' +
          '<h3 class="mapping-section__title">' + title + '</h3>' +
          '<p class="mapping-section__warn">尚未匯入資料，請先完成「匯入資料」。</p>' +
        '</section>'
      );
    }

    var committed = mappingState.committed;
    var matches = draftMatchesCommitted(mappingState, mode);

    // 已提交且草稿未偏離、未按「重新配對」→ 收合摘要卡。
    if (committed && matches && !editing[kind]) {
      return summarySection(kind, title, fields, modes, importInfo, mappingState, mode);
    }

    return Store.getState().mappingUiMode === 'grid'
      ? gridEditSection(kind, title, fields, modes, importInfo, mappingState, mode, committed, matches)
      : classicEditSection(kind, title, fields, modes, importInfo, mappingState, mode, committed, matches);
  }

  function summarySection(kind, title, fields, modes, importInfo, mappingState, mode) {
    var committed = mappingState.committed;

    var facts = [modeLabel(modes, mode)];
    if (committed.projectedRowCount != null) {
      facts.push('共 ' + Number(committed.projectedRowCount).toLocaleString() + ' 筆');
    }
    if (committed.committedUtc) {
      facts.push('確認於 ' + new Date(committed.committedUtc).toLocaleString('zh-Hant', { hour12: false }));
    }

    return (
      '<section class="mapping-section" data-bind="mapping-' + kind + '" data-motion-key="mapping-' + kind + '-summary">' +
        '<h3 class="mapping-section__title">' + title + '</h3>' +
        '<div class="mapping-summary">' +
          '<p class="mapping-summary__status">已確認配對' +
            '<span class="mapping-summary__facts">' + facts.map(Ui.esc).join('，') + '</span></p>' +
          committedTableHtml(kind, fields, importInfo, committed) +
          literalNoteHtml(fields, committed) +
          committedOptionsHtml(kind, committed) +
          '<div class="panel__actions panel__actions--start">' +
            '<button type="button" class="btn btn--ghost" data-action="preview-target-' + kind +
              '" title="' + (kind === 'gl' ? '檢視 GL' : '檢視 TB') + '">預覽</button>' +
            '<button type="button" class="btn btn--ghost" data-action="remap-' + kind + '">重新配對</button>' +
          '</div>' +
        '</div>' +
      '</section>'
    );
  }

  // 已提交 GL 配對的標準化政策摘要（唯讀）：只複述後端回傳的 canonical options，不重新推導。
  function committedOptionsHtml(kind, committed) {
    if (kind !== 'gl' || !committed || !committed.options) { return ''; }
    var options = committed.options;
    var approval = Ui.GL_APPROVAL_DATE_MODES.filter(function (m) {
      return m.value === options.approvalDateMode;
    })[0];
    var facts = [options.approvalDateMode === 'mapped' && committed.mapping && committed.mapping.docDate
      ? '核准日：來源欄「' + committed.mapping.docDate + '」'
      : '核准日：' + (approval ? approval.label : options.approvalDateMode)];

    if (options.postingStatusPolicy) {
      var accepted = options.postingStatusPolicy.acceptedValues || [];
      facts.push('過帳狀態接受 ' +
        (accepted.length ? accepted.join('、') : '（無）') +
        (options.postingStatusPolicy.includeBlank ? '，含空白' : ''));
    } else {
      facts.push('未設定過帳狀態篩選');
    }

    if (committed.mapping && committed.mapping.manual) {
      var manual = options.manualAutoPolicy || { manualValues: [], automaticValues: [] };
      facts.push(manual.unlistedValueKind === 'automatic' ? '人工 ' + manual.manualValues.join('、') + '，其餘非空白值為自動' :
        manual.unlistedValueKind === 'manual' ? '自動 ' + manual.automaticValues.join('、') + '，其餘非空白值為人工' :
        '人工 ' + manual.manualValues.join('、') + '／自動 ' + manual.automaticValues.join('、'));
      facts.push('空白：' + ({ manual: '視為人工', automatic: '視為自動', unclassified: '不判定人工或自動', reject: '先補齊來源資料' }[manual.blankValueKind || 'reject']));
    }

    var rde = options.rdeFields || [];
    facts.push(rde.length
      ? '攸關資料元素欄位 ' + rde.length + ' 個：' + rde.map(function (f) { return f.label; }).join('、')
      : '未保留額外的攸關資料元素欄位');

    return '<ul class="mapping-summary__options" data-bind="gl-committed-options">' +
      facts.map(function (fact) { return '<li>' + Ui.esc(fact) + '</li>'; }).join('') + '</ul>';
  }

  // 唯讀二維對照表:來源欄當表頭、其下標示對應到的 JET 欄位,再附樣本資料列(沿用 sourceCache)。
  function committedTableHtml(kind, fields, importInfo, committed) {
    var columns = (importInfo && importInfo.columns) || [];
    var span = Math.max(columns.length, 1);

    var assignRow = columns.map(function (col) {
      var field = fieldForCommitted(fields, committed.mapping, col);
      return '<th class="map-grid__cell' + (field ? ' is-assigned' : '') + '">' +
        (field ? Ui.esc(field.label) : '<span class="map-grid__unassigned">(未對應)</span>') +
        '</th>';
    }).join('');

    var headRow = columns.map(function (col) {
      return '<th class="map-grid__colname">' + Ui.esc(col) + '</th>';
    }).join('');

    var cache = sourceCache[kind];
    var bodyHtml;
    if (!cache || (importInfo && cache.batchId !== importInfo.batchId) || (cache.loading && !cache.rows)) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">載入預覽中…</td></tr>';
    } else if (cache.error && !cache.rows) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">預覽載入失敗，請重試。</td></tr>';
    } else if (!cache.rows.length) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">最近一次匯入沒有資料列。</td></tr>';
    } else {
      bodyHtml = cache.rows.map(function (row) {
        return '<tr>' + columns.map(function (col, i) {
          var cell = row[i];
          return '<td class="map-grid__data' + (cell == null ? ' is-null' : '') + '">' +
            (cell == null ? '∅' : Ui.esc(cell)) + '</td>';
        }).join('') + '</tr>';
      }).join('');
    }

    var note = sourcePagerHtml(kind, cache);

    return '<div class="map-grid-wrap" data-preserve-scroll="map-grid-' + kind + '"><table class="map-grid">' +
      '<thead><tr class="map-grid__assign">' + assignRow + '</tr>' +
      '<tr class="map-grid__head">' + headRow + '</tr></thead>' +
      '<tbody>' + bodyHtml + '</tbody></table></div>' + note;
  }

  // 反查:某來源欄被哪個(非字面值)JET 欄位對應到(比對 committed.mapping)。
  function fieldForCommitted(fields, mappingObj, column) {
    var literalKeys = {};
    fields.forEach(function (f) { if (f.literal) { literalKeys[f.key] = true; } });
    var hitKey = Object.keys(mappingObj || {}).filter(function (k) {
      return !literalKeys[k] && mappingObj[k] === column;
    })[0];
    return hitKey ? fields.filter(function (f) { return f.key === hitKey; })[0] : null;
  }

  // 借方與貸方代碼不是來源欄，於表下補述各自的值。
  function literalNoteHtml(fields, committed) {
    var notes = fields.filter(function (f) {
      return f.literal && committed.mapping && committed.mapping[f.key];
    }).map(function (f) {
      return Ui.esc(f.label) + ' = ' + Ui.esc(committed.mapping[f.key]);
    });
    return notes.length
      ? '<p class="mapping-summary__literal">' + notes.join('，') + '</p>'
      : '';
  }

  function twoDimMappingTable(kind, fields, importInfo, mappingState, mode) {
    var columns = importInfo.columns || [];
    var assignable = assignableFields(fields, mode);
    var span = Math.max(columns.length, 1);

    var assignRow = columns.map(function (col) {
      var assigned = fieldForColumn(fields, mappingState, col);
      var shared = assignable.filter(function (field) { return mappingState.draft[field.key] === col; });
      if (shared.length > 1) {
        return '<th class="map-grid__cell is-assigned"><span class="map-grid__shared">' +
          shared.map(function (field) { return Ui.esc(field.label); }).join('、') +
          '</span><small>共用此來源欄，可在「簡易清單」調整。</small></th>';
      }
      var opts = '<option value="">（不對應）</option>' + assignable.map(function (f) {
        return '<option value="' + f.key + '"' + (f.key === assigned ? ' selected' : '') + '>' +
          Ui.esc(f.label) + '</option>';
      }).join('');
      return '<th class="map-grid__cell' + (assigned ? ' is-assigned' : '') + '">' +
        '<select class="map-grid__select" data-map-col="' + Ui.esc(col) +
          '" aria-label="來源欄「' + Ui.esc(col) + '」對應的 JET 欄位"' +
          ' data-focus-key="map-grid-' + kind + '-' + Ui.esc(col) + '">' + opts + '</select>' +
        '</th>';
    }).join('');

    var headRow = columns.map(function (col) {
      return '<th class="map-grid__colname">' + Ui.esc(col) + '</th>';
    }).join('');

    var cache = sourceCache[kind];
    var bodyHtml;
    if (!cache || cache.batchId !== importInfo.batchId || (cache.loading && !cache.rows)) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">載入預覽中…</td></tr>';
    } else if (cache.error && !cache.rows) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">預覽載入失敗，請重試。</td></tr>';
    } else if (!cache.rows.length) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">最近一次匯入沒有資料列。</td></tr>';
    } else {
      bodyHtml = cache.rows.map(function (row) {
        return '<tr>' + columns.map(function (col, i) {
          var cell = row[i];
          return '<td class="map-grid__data' + (cell == null ? ' is-null' : '') + '">' +
            (cell == null ? '∅' : Ui.esc(cell)) + '</td>';
        }).join('') + '</tr>';
      }).join('');
    }

    var note = sourcePagerHtml(kind, cache);

    return '<div class="map-grid-wrap" data-preserve-scroll="map-grid-' + kind + '"><table class="map-grid">' +
      '<thead><tr class="map-grid__assign">' + assignRow + '</tr>' +
      '<tr class="map-grid__head">' + headRow + '</tr></thead>' +
      '<tbody>' + bodyHtml + '</tbody></table></div>' + note +
      (kind === 'gl' && !mappingState.draft.docDate
        ? '<label class="map-date-purpose"><input type="checkbox" data-approval-same-date data-focus-key="gl-approval-same-date"' +
          (glOptions().approvalDateMode === 'sameAsPostDate' ? ' checked' : '') + '>核准日與總帳日期相同</label>' : '');
  }

  // 二維表右側必填鐵軌：每個當前模式必填欄位一列；未指派灰「待指派」，已指派綠「✓ + 來源欄名」。
  // 清單與每一列都有識別：換金額欄位格式時多出來的欄位長出來、不再需要的收起來，標題原地交叉淡化（app.js 的 JetMotion）。
  function requiredRailHtml(eligibility, mappingState, kind) {
    var items = eligibility.required.map(function (f) {
      var val = mappingState.draft[f.key];
      var done = !!val;
      var tail = done
        ? '<span class="map-rail__col">' + Ui.esc(val) + '</span>'
        : '<span class="map-rail__pending">待指派</span>';
      return '<li class="map-rail__item' + (done ? ' is-done' : '') + '" data-motion-key="' + Ui.esc('map-rail-' + kind + '-' + f.key) + '">' +
        '<span class="map-rail__mark" aria-hidden="true">' + (done ? '✓' : '') + '</span>' +
        '<button type="button" class="map-rail__label map-rail__jump" title="在簡易清單中定位來源欄位" data-focus-mapping-field="' + Ui.esc(f.key) + '">' + Ui.esc(f.label) + '</button>' +
        tail + '</li>';
    }).join('');

    var title = eligibility.missing.length > 0
      ? '尚缺 ' + eligibility.missing.length + ' 個必填欄位'
      : (eligibility.canCommit ? '必填欄位皆已配對' : '欄位已指派，請確認下方設定');
    return '<aside class="map-rail' + (eligibility.canCommit ? ' is-complete' : '') + '" data-motion-key="map-rail-' + kind + '">' +
      '<h4 class="map-rail__title">' + title + '</h4>' +
      '<ul class="map-rail__list">' + items + '</ul></aside>';
  }

  // 標準化政策未補齊時的就近提示（與必填鐵軌同一位置語彙）；後端仍是權威驗證。
  function optionProblemsHtml(eligibility, kind) {
    if (!eligibility.problems || !eligibility.problems.length) { return ''; }
    return '<p class="form-notice" data-bind="mapping-option-problems" data-motion-key="mapping-option-problems-' + kind + '">尚需補齊：' +
      eligibility.problems.map(Ui.esc).join('、') + '</p>';
  }

  // 已知問題先就地提醒；不把提醒提升成新的前端禁止條件，最後仍由確認配對的後端判定。
  function mappingPreflightHtml(kind) {
    if (kind !== 'gl') { return ''; }
    var column = Store.getState().mapping.gl.draft.manual;
    if (!column) { return ''; }
    var policy = glOptions().manualAutoPolicy, warnings = [], conflicts = [];
    policy.manualValues.forEach(function (value) {
      if (policy.automaticValues.some(function (other) { return sameCode(value, other, column); }) &&
          !conflicts.some(function (other) { return sameCode(value, other, column); })) { conflicts.push(value); }
    });
    if (conflicts.length) { warnings.push('人工與自動代碼重複：「' + conflicts.join('、') + '」。請保留在正確的一側後再確認。'); }
    var profile = valueProfiles('gl')[column];
    // V3、Q5：只列一側時，沒列出的值都歸到另一側。代碼還沒和來源核對，就在確認前說清楚後果；不擋確認，
    // 確認後後端若發現代碼一筆都沒有，會再提醒一次。
    var listsManual = policy.unlistedValueKind === 'automatic';
    var listed = listsManual ? policy.manualValues : (policy.unlistedValueKind === 'manual' ? policy.automaticValues : []);
    if (listed.length && (!profile || profile.metadataOnly || profile.error)) {
      // 使用者 2026-10-14 第三輪勾選頁確認 R3-42；同日再要求黃色提示不過度解釋，拿掉「可先按「讀取來源值」確認」，
      // 按鈕就在旁邊。讀取失敗時仍要說可以再試一次。
      warnings.push((listsManual ? '人工' : '自動') + '分錄代碼「' + listed.join('、') + '」尚未與來源核對；來源沒有' +
        (listed.length > 1 ? '這些' : '此') + '代碼時，有值的列都會視為' + (listsManual ? '自動' : '人工') +
        '分錄。' + (profile && profile.error ? '讀取來源值失敗，可按「再試一次」。' : ''));
    }
    if (profile && !profile.metadataOnly && !profile.loading && !profile.error) {
      var missing = policyCodes(column).filter(function (value) { return missingSourceCode(value, column); });
      if (missing.length) { warnings.push('「' + missing.join('、') + '」：來源裡沒有這個值，請確認是否需要修改。'); }
      if (profile.blankCount > 0 && (policy.blankValueKind || 'reject') === 'reject') {
        warnings.push('來源空白有 ' + Number(profile.blankCount).toLocaleString() + ' 列，請在「來源空白時」選擇處理方式。');
      }
      if ((policy.unlistedValueKind || 'reject') === 'reject' && !missingComparisonCodes(profile, column).length) {
        var unclassified = (profile.values || []).filter(function (item) {
          return !policy.manualValues.concat(policy.automaticValues).some(function (value) { return sameCode(value, item.value, column); });
        }).map(function (item) { return item.value; });
        if (unclassified.length) { warnings.push('「' + unclassified.join('、') + '」未歸類為人工或自動，請歸類，或改用只列一側。'); }
      }
    }
    // 這些是不擋確認的提醒，用「需要注意」的琥珀色；檢查真的沒通過時另有紅色的配對未完成（使用者 2026-10-09 裁定）。
    return warnings.length ? '<div class="form-notice form-notice--warning" data-bind="mapping-preflight-gl" data-motion-key="mapping-preflight-gl" role="status"><strong>確認前請留意：</strong><ul>' +
      warnings.map(function (warning) { return '<li>' + Ui.esc(warning) + '</li>'; }).join('') + '</ul></div>' : '';
  }

  // 提交錯誤綁定當次案件與匯入來源；換案件或重新匯入後作廢。只在 render 開頭呼叫一次，
  // 畫面函式本身只讀不寫。
  function discardStaleMappingCommitErrors(state) {
    ['gl', 'tb'].forEach(function (kind) {
      var error = mappingCommitErrors[kind];
      if (error && (error.project !== state.project || error.importInfo !== state.importState[kind])) {
        mappingCommitErrors[kind] = null;
      }
    });
  }

  function mappingCommitErrorHtml(kind) {
    var error = mappingCommitErrors[kind];
    if (!error) { return ''; }
    var head = '<strong>' + (kind === 'gl' ? 'GL' : 'TB') + ' 配對未完成：</strong>' +
      (error.modified ? '<p role="status">設定已修改，請重新確認。下面是上次確認時的問題。</p>' : '');
    var groups = Array.isArray(error.details) ? error.details.filter(function (detail) {
      return detail && detail.message;
    }) : [];
    if (!groups.length) {
      return '<div class="form-notice mapping-section__error" data-bind="mapping-commit-error-' + kind +
        '" data-motion-key="mapping-error-' + kind + '" role="alert">' + head + Ui.esc(error.message) + '</div>';
    }
    // 後端的 message 是開頭一句加上各組文字；拿掉各組文字就是開頭那句（例如「13 列無法轉換…」）。
    var lead = groups.reduce(function (text, detail) {
      return text.replace(detail.message, '');
    }, error.message || '').trim();
    var mappingState = Store.getState().mapping[kind];
    return '<div class="form-notice mapping-section__error" data-bind="mapping-commit-error-' + kind +
      '" data-motion-key="mapping-error-' + kind + '" role="alert">' + head + Ui.esc(lead) +
      '<ul class="mapping-error-groups">' + groups.map(function (detail) {
        var target = mappingErrorTarget(kind, mappingState, detail.sourceColumn, detail.reasonCode);
        return '<li class="mapping-error-groups__item"><span>' + Ui.esc(detail.message) + '</span>' +
          (target
            ? '<button type="button" class="btn btn--ghost btn--tiny" ' + target.attribute + '="' +
                Ui.esc(target.value) + '" aria-label="前往「' + Ui.esc(detail.sourceColumn) + '」的設定">前往設定</button>'
            : '') +
          '</li>';
      }).join('') + '</ul></div>';
  }

  // 無法轉換的來源欄在畫面上要到哪裡改：人工/自動分錄看判定方式，攸關資料元素看型別，
  // 其他核心欄位看該欄位的來源欄選單。找不到對應位置時不放按鈕。
  function mappingErrorTarget(kind, mappingState, sourceColumn, reasonCode) {
    if (!sourceColumn) { return null; }
    var draft = mappingState.draft || {};
    if (kind === 'gl' && draft.manual === sourceColumn) {
      return { attribute: 'data-focus-mapping-option', value: reasonCode === 'manual_blank' ? 'manual-blank' : 'manual-mode' };
    }
    var rde = kind === 'gl' && mappingState.options && (mappingState.options.rdeFields || []).some(function (field) {
      return field.sourceColumn === sourceColumn;
    });
    if (rde) {
      return { attribute: 'data-focus-mapping-option', value: 'rde-type-' + sourceColumn };
    }
    var key = Object.keys(draft).find(function (candidate) {
      return candidate !== 'dcDebitCode' && candidate !== 'dcCreditCode' && draft[candidate] === sourceColumn;
    });
    return key ? { attribute: 'data-focus-mapping-field', value: key } : null;
  }

  function markMappingCommitErrorModified(kind, section) {
    var error = mappingCommitErrors[kind];
    if (!error) { return; }
    var notice = Ui.findLive(section, '[data-bind="mapping-commit-error-' + kind + '"]');
    // 畫面上這份提示是用哪段 HTML 換上的：這裡換上的節點記在節點上；整步重繪產生的節點沒有記號，
    // 用改動前的狀態算一次，就是重繪當時放上去的內容。
    var shown = notice ? notice.__jetCommitErrorHtml : undefined;
    if (notice && typeof shown !== 'string') { shown = mappingCommitErrorHtml(kind); }
    error.modified = true;
    if (!notice) { return; }
    var html = mappingCommitErrorHtml(kind);
    // 內容沒變就不換。select、勾選框改值時會先後發 input 與 change，打字也是一字一次；
    // 每次都換掉節點會切斷正在長高的動態，提示又一下跳到完整高度。
    if (html === shown) {
      notice.__jetCommitErrorHtml = html;
      return;
    }
    function update() {
      notice.outerHTML = html;
      var fresh = Ui.findLive(section, '[data-bind="mapping-commit-error-' + kind + '"]');
      if (fresh) { fresh.__jetCommitErrorHtml = html; }
    }
    // 內容變了（第一次修改多出「設定已修改」一行，或「前往設定」按鈕跟著草稿改變）時走 JetMotion，
    // 提示框從原本的高度長到新的高度，下面的內容跟著移動，不再一下跳開（使用者 2026-10-13 要求展開與收合一致）。
    // section 本身有 data-motion-key（mapping-<kind>-edit），提示是 mapping-error-<kind>。
    if (global.JetMotion && typeof global.JetMotion.morph === 'function') {
      global.JetMotion.morph(section, update);
    } else {
      update();
    }
  }

  // 編輯畫面頂端的狀態提示。重建整步與輸入時的就地更新共用這一份，兩條路徑的文字不會分歧。
  // 同次開啟保留目前草稿；重開案件才帶回上次確認的配對。缺少的來源欄逐一列出，讓審計員補選。
  function mappingBannerHtml(kind, mappingState, committed, matches) {
    var dropped = mappingState.droppedFields || [];
    var droppedText = dropped.length
      ? '新資料沒有這些來源欄，請重新選擇：' + dropped.map(function (item) {
          return droppedFieldLabel(kind, item) + '（原為「' + item.column + '」）';
        }).join('、') + '。'
      : '';
    var text = mappingState.invalidatedByImport
      ? '來源資料已變更，請檢查配對後按「確認配對」。' + droppedText
      : (committed && !matches ? '配對已修改，請按「重新確認配對」儲存。'
        : (droppedText ? '來源資料已變更。' + droppedText : ''));
    // 提示會因為修改配對而出現或消失，標上識別，出現時從原處長出來、消失時收回去，下面的內容跟著平順移動。
    if (text) {
      return '<p class="mapping-section__warn" data-bind="mapping-banner-' + kind + '" data-motion-key="mapping-banner-' + kind + '">' +
        Ui.esc(text) + '</p>';
    }
    return '';
  }

  function droppedFieldLabel(kind, item) {
    if (item.rde) { return '攸關資料元素「' + (item.label || item.column) + '」'; }
    var field = (kind === 'gl' ? Ui.GL_FIELDS : Ui.TB_FIELDS).find(function (candidate) {
      return candidate.key === item.key;
    });
    return field ? field.label : item.key;
  }

  function gridEditSection(kind, title, fields, modes, importInfo, mappingState, mode, committed, matches) {
    var banner = mappingBannerHtml(kind, mappingState, committed, matches);

    var literalHtml = literalFieldFor(fields, mode).map(function (litField) {
      var litVal = mappingState.draft[litField.key] || '';
      var litId = 'mapping-' + kind + '-' + litField.key + '-literal';
      var example = litField.key === 'dcCreditCode' ? 'C 或 2' : 'D 或 1';
      return '<div class="map-literal">' +
        '<label class="map-literal__label" for="' + litId + '">' + Ui.esc(litField.label) + '</label>' +
        '<input class="form__input map-literal__input" type="text" id="' + litId + '" data-mapping-key="' + litField.key +
          '" aria-label="' + Ui.esc(litField.label) + '" aria-describedby="' + litId + '-hint"' +
          ' data-focus-key="mapping-' + kind + '-' + litField.key + '" value="' +
          Ui.esc(litVal) + '" placeholder="如 ' + example + '">' +
        '<span class="map-literal__hint" id="' + litId + '-hint">輸入代表' +
          (litField.key === 'dcCreditCode' ? '貸方' : '借方') + '的代碼，例如 ' + example + '</span></div>';
    }).join('');

    var eligibility = mappingCommitEligibility(fields, mode, mappingState, kind);

    var actions =
      '<button type="button" class="btn btn--ghost panel__action--start" data-action="preview-source-' + kind +
        '" title="開啟資料預覽，對照欄位名稱與實際內容">預覽</button>' +
      (committed
        ? '<button type="button" class="btn btn--ghost" data-action="restore-' + kind + '">取消變更</button>'
        : '') +
      '<button type="button" class="btn" data-action="commit-' + kind + '"' +
        (eligibility.canCommit ? '' : ' disabled') + '>' +
        (committed ? '重新確認配對' : '確認配對') + '</button>';

    // 對照表那一塊（map-layout）標上識別：換金額欄位格式或換表格樣式時，表格原地交叉淡化、高度平順改變，
    // 下面的設定跟著移動，不再一下跳過去（app.js 的 JetMotion，使用者 2026-10-12）。
    return '<section class="mapping-section" data-bind="mapping-' + kind + '" data-motion-key="mapping-' + kind + '-edit">' +
        '<div class="mapping-section__toolbar"><h3 class="mapping-section__title">' + title + '</h3>' +
          uiModeToggleHtml(kind) + '</div>' +
        banner +
        modeGroupHtml(kind, modes, mode) +
        '<div class="map-layout map-layout--grid" data-motion-key="map-layout-' + kind + '"' + mapLayoutPanelAttributes(kind) + '>' +
          '<div class="map-layout__main">' +
            twoDimMappingTable(kind, fields, importInfo, mappingState, mode) +
            literalHtml +
          '</div>' +
          requiredRailHtml(eligibility, mappingState, kind) +
        '</div>' +
        (kind === 'gl' ? glOptionsHtml(importInfo, mappingState) : '') +
        optionProblemsHtml(eligibility, kind) +
        mappingPreflightHtml(kind) +
        mappingCommitErrorHtml(kind) +
        '<div class="panel__actions">' + actions + '</div>' +
      '</section>';
  }

  function classicEditSection(kind, title, fields, modes, importInfo, mappingState, mode, committed, matches) {
    var banner = mappingBannerHtml(kind, mappingState, committed, matches);

    var eligibility = mappingCommitEligibility(fields, mode, mappingState, kind);
    var rows = fieldsForMode(fields, mode).map(function (field) {
      var required = !field.literal && Ui.isRequired(field, mode);
      var current = mappingState.draft[field.key] || '';

      var control;
      if (kind === 'gl' && field.key === 'docDate') {
        control = approvalChoiceHtml(glOptions(), mappingState.draft, importInfo);
      } else if (field.literal) {
        control = '<input class="form__input mapping-table__input" type="text" data-mapping-key="' + field.key +
          '" aria-label="' + Ui.esc(field.label) + '"' +
          ' data-focus-key="mapping-' + kind + '-' + field.key + '" value="' +
          Ui.esc(current) + '" placeholder="如 ' + (field.key === 'dcCreditCode' ? 'C 或 2' : 'D 或 1') + '">';
      } else {
        var sourceOptions = importInfo.columns.map(function (col) {
          return '<option value="' + Ui.esc(col) + '"' + (col === current ? ' selected' : '') + '>' +
            Ui.esc(col) + '</option>';
        }).join('');
        var options = kind === 'gl' && field.key === 'lineID'
          ? '<optgroup label="系統產生"><option value="">自動編號</option></optgroup>' +
            '<optgroup label="來源欄位">' + sourceOptions + '</optgroup>'
          : '<option value="">—</option>' + sourceOptions;
        // 名稱取自欄位目錄的標籤，報讀器才知道這是哪個 JET 欄位的來源欄。
        control = '<select class="mapping-table__select" data-mapping-key="' + field.key +
          '" aria-label="' + Ui.esc(field.label) + '的來源欄"' +
          ' data-focus-key="mapping-' + kind + '-' + field.key + '">' +
          options + '</select>';
      }

      return '<tr class="mapping-table__row' + (required ? ' is-required' : '') + '">' +
          '<td class="mapping-table__label">' + field.label +
            (required ? ' <em class="form__req">*</em>' : '') + '</td>' +
          '<td>' + control + '</td>' +
        '</tr>';
    }).join('');

    var actions =
      '<button type="button" class="btn btn--ghost panel__action--start" data-action="preview-source-' + kind +
        '" title="開啟資料預覽，對照欄位名稱與實際內容">預覽</button>' +
      (committed
        ? '<button type="button" class="btn btn--ghost" data-action="restore-' + kind + '">取消變更</button>'
        : '') +
      '<button type="button" class="btn" data-action="commit-' + kind + '"' +
        (eligibility.canCommit ? '' : ' disabled') + '>' +
        (committed ? '重新確認配對' : '確認配對') + '</button>';

    // 對照表那一塊（map-layout）標上識別：換金額欄位格式或換表格樣式時，表格原地交叉淡化、高度平順改變，
    // 下面的設定跟著移動，不再一下跳過去（app.js 的 JetMotion，使用者 2026-10-12）。
    return '<section class="mapping-section" data-bind="mapping-' + kind + '" data-motion-key="mapping-' + kind + '-edit">' +
        '<div class="mapping-section__toolbar"><h3 class="mapping-section__title">' + title + '</h3>' +
          uiModeToggleHtml(kind) + '</div>' +
        banner +
        modeGroupHtml(kind, modes, mode) +
        '<div class="map-layout" data-motion-key="map-layout-' + kind + '"' + mapLayoutPanelAttributes(kind) + '>' +
          '<div class="map-layout__main mapping-table-wrap" data-preserve-scroll="mapping-table-' + kind + '"><table class="mapping-table">' +
            '<thead><tr><th>JET 欄位</th><th>來源欄位</th></tr></thead>' +
            '<tbody>' + rows + '</tbody>' +
          '</table></div>' +
          requiredRailHtml(eligibility, mappingState, kind) +
        '</div>' +
        (kind === 'gl' ? glOptionsHtml(importInfo, mappingState) : '') +
        optionProblemsHtml(eligibility, kind) +
        mappingPreflightHtml(kind) +
        mappingCommitErrorHtml(kind) +
        '<div class="panel__actions">' + actions + '</div>' +
      '</section>';
  }

  // mapping.commit.gl 的 additive payload：只有配對過帳狀態時才帶 policy（後端規定
  // 未配對時不得提供）；rdeFields 只送使用者勾選的欄位，新欄省略 fieldId。
  function glCommitPayload(mapping, mode) {
    var options = glOptions();
    var payload = {
      mapping: mapping,
      amountMode: mode,
      approvalDateMode: options.approvalDateMode,
      manualAutoPolicy: Object.assign({}, options.manualAutoPolicy, {
        manualValues: options.manualAutoPolicy.manualValues.slice(),
        automaticValues: options.manualAutoPolicy.automaticValues.slice()
      }),
      rdeFields: (options.rdeFields || []).map(function (field) {
        var wire = {
          sourceColumn: field.sourceColumn,
          label: String(field.label == null ? '' : field.label).trim(),
          valueType: field.valueType
        };
        if (field.fieldId) { wire.fieldId = field.fieldId; }
        return wire;
      })
    };
    if (mapping.postingStatus) {
      var policy = options.postingStatusPolicy || { acceptedValues: [], includeBlank: false };
      payload.postingStatusPolicy = {
        acceptedValues: (policy.acceptedValues || []).slice(),
        includeBlank: !!policy.includeBlank
      };
    }
    return payload;
  }

  // commit response 的 canonical options 鏡像（含後端產生的 stable field ID）。
  function canonicalGlOptions(data) {
    return {
      approvalDateMode: data.approvalDateMode,
      postingStatusPolicy: data.postingStatusPolicy || null,
      manualAutoPolicy: data.manualAutoPolicy,
      rdeFields: (data.rdeFields || []).map(function (field) {
        return {
          fieldId: field.fieldId,
          sourceColumn: field.sourceColumn,
          label: field.label,
          valueType: field.valueType
        };
      })
    };
  }

  /* ---- 事件 ------------------------------------------------------------------ */

  /* ---- GL 標準化政策的事件 ----------------------------------------------------- */

  // 原始草稿文字保留；已知等價關係只採後端結果，未知值交由完成配對時的正準化處理。
  function toggleCodeValue(values, value, include, column) {
    var raw = String(value == null ? '' : value);
    if (!raw.length) { return values.slice(); }
    var next = values.filter(function (item) {
      return !sameCode(item, raw, column);
    });
    if (include) { next.push(raw); }
    return next;
  }

  function setPostingStatusPolicy(patch) {
    var current = glOptions().postingStatusPolicy || { acceptedValues: [], includeBlank: false };
    Store.patchGlMappingOptions({
      postingStatusPolicy: {
        acceptedValues: patch.acceptedValues || current.acceptedValues,
        includeBlank: typeof patch.includeBlank === 'boolean' ? patch.includeBlank : current.includeBlank
      }
    });
  }

  function bindGlOptions(section) {
    var manualSource = Store.getState().mapping.gl.draft.manual;
    var postingSource = Store.getState().mapping.gl.draft.postingStatus;
    ensureCodeComparisons(manualSource);
    ensureCodeComparisons(postingSource);
    ensureDcCodeComparisons();
    ensureSourceCodePresence(manualSource);
    section.querySelectorAll('[data-retry-source-check]').forEach(function (button) {
      button.addEventListener('click', function () { ensureSourceCodePresence(button.getAttribute('data-retry-source-check'), true); });
    });
    section.querySelectorAll('[data-retry-comparison]').forEach(function (button) {
      button.addEventListener('click', function () { ensureCodeComparisons(button.getAttribute('data-retry-comparison'), true); });
    });
    [['manual-code-add', 'manual'], ['automatic-code-add', 'automatic'], ['posting-value-add', 'posting']].forEach(function (item) {
      var input = section.querySelector('[data-bind="' + item[0] + '"]');
      if (input) {
        input.addEventListener('input', function () {
          var drafts = codeInputDrafts(); drafts[item[1]] = input.value;
          if (item[1] !== 'posting') { drafts.manualEditor = 'codes'; }
        });
      }
    });
    var sameDate = section.querySelector('[data-approval-same-date]');
    if (sameDate) { sameDate.addEventListener('change', function () {
      Store.patchGlMappingOptions({ approvalDateMode: sameDate.checked ? 'sameAsPostDate' : 'unmapped' });
    }); }
    var approvalChoice = section.querySelector('[data-approval-choice]');
    if (approvalChoice) {
      approvalChoice.addEventListener('change', function () {
        var value = approvalChoice.value;
        if (value.indexOf('column:') === 0) {
          // 選了來源欄：草稿寫入 docDate，syncApprovalSource 同步改成「由來源欄提供」。
          Store.setMappingDraft('gl', 'docDate', value.slice('column:'.length));
        } else {
          // 模式與來源欄在同一次 store 更新內改變，避免重繪到互相矛盾的中間狀態；非 mapped 時 store 會清掉 docDate。
          Store.patchGlMappingOptions({ approvalDateMode: value.slice('mode:'.length) });
        }
      });
    }

    section.querySelectorAll('[data-action="load-posting-profile"]').forEach(function (button) {
      button.addEventListener('click', function () {
        ensureValueProfile('gl', Store.getState().mapping.gl.draft.postingStatus);
      });
    });

    ['codes', 'values'].forEach(function (mode) {
      var button = section.querySelector('[data-action="manual-entry-' + mode + '"]');
      if (button) { button.addEventListener('click', function () {
        codeInputDrafts().manualEditor = mode;
        Store.touch();
      }); }
    });

    section.querySelectorAll('[data-action="load-manual-profile"]').forEach(function (button) {
      button.addEventListener('click', function () {
        var drafts = codeInputDrafts();
        drafts.manualEditor = drafts.manual || drafts.automatic ? 'codes' : null;
        ensureValueProfile('gl', Store.getState().mapping.gl.draft.manual);
      });
    });

    section.querySelectorAll('[data-posting-value]').forEach(function (checkbox) {
      checkbox.addEventListener('change', function () {
        var profile = valueProfiles('gl')[postingSource];
        if (!profile || missingComparisonCodes(profile, postingSource).length) { return; }
        var policy = glOptions().postingStatusPolicy || { acceptedValues: [], includeBlank: false };
        setPostingStatusPolicy({
          acceptedValues: toggleCodeValue(
            policy.acceptedValues || [], checkbox.getAttribute('data-posting-value'), checkbox.checked, postingSource)
        });
      });
    });

    section.querySelectorAll('[data-remove-posting-value]').forEach(function (button) {
      button.addEventListener('click', function () {
        var policy = glOptions().postingStatusPolicy || { acceptedValues: [], includeBlank: false };
        setPostingStatusPolicy({
          acceptedValues: toggleCodeValue(
            policy.acceptedValues || [], button.getAttribute('data-remove-posting-value'), false, postingSource)
        });
      });
    });

    var addPostingValue = section.querySelector('[data-action="add-posting-value"]');
    if (addPostingValue) {
      addPostingValue.addEventListener('click', function () {
        var input = section.querySelector('[data-bind="posting-value-add"]');
        if (!input || !input.value.length) { return; }
        var policy = glOptions().postingStatusPolicy || { acceptedValues: [], includeBlank: false };
        codeInputDrafts().posting = '';
        setPostingStatusPolicy({
          acceptedValues: toggleCodeValue(policy.acceptedValues || [], input.value, true, postingSource)
        });
      });
    }

    var includeBlank = section.querySelector('[data-option-bind="includeBlank"]');
    if (includeBlank) {
      includeBlank.addEventListener('change', function () {
        setPostingStatusPolicy({ includeBlank: includeBlank.checked });
      });
    }

    var manualMode = section.querySelector('[data-manual-mode]');
    if (manualMode) { manualMode.addEventListener('change', function () {
      var policy = glOptions().manualAutoPolicy;
      Store.patchGlMappingOptions({ manualAutoPolicy: Object.assign({}, policy, {
        unlistedValueKind: manualMode.value,
        manualValues: manualMode.value === 'manual' ? [] : policy.manualValues.slice(),
        automaticValues: manualMode.value === 'automatic' ? [] : policy.automaticValues.slice()
      }) });
    }); }
    var manualBlank = section.querySelector('[data-manual-blank]');
    if (manualBlank) { manualBlank.addEventListener('change', function () {
      Store.patchGlMappingOptions({ manualAutoPolicy: Object.assign({}, glOptions().manualAutoPolicy, {
        blankValueKind: manualBlank.value
      }) });
    }); }
    section.querySelectorAll('[data-manual-include]').forEach(function (checkbox) {
      checkbox.addEventListener('change', function () {
        var profile = valueProfiles('gl')[manualSource];
        if (!profile || missingComparisonCodes(profile, manualSource).length) { return; }
        var policy = glOptions().manualAutoPolicy;
        var key = policy.unlistedValueKind === 'automatic' ? 'manualValues' : 'automaticValues';
        var patch = {}; patch[key] = toggleCodeValue(policy[key], checkbox.getAttribute('data-manual-include'), checkbox.checked, manualSource);
        Store.patchGlMappingOptions({ manualAutoPolicy: Object.assign({}, policy, patch) });
      });
    });
    section.querySelectorAll('[data-manual-assign]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        if (!radio.checked) { return; }
        var profile = valueProfiles('gl')[manualSource];
        if (!profile || missingComparisonCodes(profile, manualSource).length) { return; }
        var value = radio.getAttribute('data-manual-assign');
        var policy = glOptions().manualAutoPolicy;
        Store.patchGlMappingOptions({
          manualAutoPolicy: Object.assign({}, policy, {
            manualValues: toggleCodeValue(policy.manualValues, value, radio.value === 'manual', manualSource),
            automaticValues: toggleCodeValue(policy.automaticValues, value, radio.value === 'automatic', manualSource)
          })
        });
      });
    });

    var codeEdits = {};
    section.querySelectorAll('[data-manual-code-edit]').forEach(function (input) {
      var group = input.getAttribute('data-code-group');
      if (!codeEdits[group]) { codeEdits[group] = glOptions().manualAutoPolicy[group === 'manual' ? 'manualValues' : 'automaticValues'].slice(); }
      var index = Number(input.getAttribute('data-code-index'));
      input.addEventListener('input', function () {
        codeEdits[group][index] = input.value;
        var patch = {}; patch[group === 'manual' ? 'manualValues' : 'automaticValues'] = codeEdits[group].filter(function (value) { return value.length > 0; });
        keepEditing('gl');
        Store.patchGlMappingOptionsQuiet({ manualAutoPolicy: Object.assign({}, glOptions().manualAutoPolicy, patch) });
        refreshMappingDerived(section, 'gl', Ui.GL_FIELDS);
      });
      input.addEventListener('change', function () { ensureCodeComparisons(manualSource, false, true); });
    });

    section.querySelectorAll('[data-action="add-code"]').forEach(function (button) {
      button.addEventListener('click', function () {
        var group = button.getAttribute('data-code-group');
        var input = section.querySelector('[data-bind="' + group + '-code-add"]');
        if (!input || !input.value.length) { return; }
        var policy = glOptions().manualAutoPolicy;
        codeInputDrafts()[group] = '';
        Store.patchGlMappingOptions({
          manualAutoPolicy: Object.assign({}, policy, {
            manualValues: group === 'manual'
              ? toggleCodeValue(policy.manualValues, input.value, true, manualSource) : policy.manualValues.slice(),
            automaticValues: group === 'automatic'
              ? toggleCodeValue(policy.automaticValues, input.value, true, manualSource) : policy.automaticValues.slice()
          })
        });
        ensureCodeComparisons(manualSource, false, true);
        Store.touch();
      });
    });

    section.querySelectorAll('[data-rde-column]').forEach(function (checkbox) {
      checkbox.addEventListener('change', function () {
        var column = checkbox.getAttribute('data-rde-column');
        var fields = (glOptions().rdeFields || []).filter(function (f) {
          return f.sourceColumn !== column;
        });
        if (checkbox.checked) {
          // 新勾選的欄位刻意不帶 fieldId：後端只接受目前案件既有的 stable ID，新欄一律由後端產生。
          fields.push({ sourceColumn: column, label: column, valueType: 'text' });
        }
        Store.patchGlMappingOptions({ rdeFields: fields });
      });
    });

    var creationDate = section.querySelector('[data-rde-created-date]');
    if (creationDate) { creationDate.addEventListener('change', function () { selectCreationDateRde(creationDate.value); }); }
    var creationRequest = currentCreationDateRequest();
    var confirmCreation = section.querySelector('[data-action="confirm-created-date"]');
    if (confirmCreation) { confirmCreation.addEventListener('click', function () {
      if (!creationRequest || currentCreationDateRequest() !== creationRequest) { return; }
      pendingCreationDate = null;
      mappingCommitErrors.gl = null;
      Store.patchGlMappingOptions({ rdeFields: (glOptions().rdeFields || []).map(function (field) {
        return field.sourceColumn === creationRequest.column
          ? Object.assign({}, field, { label: '傳票建立日', valueType: 'date' }) : field;
      }) });
    }); }
    var cancelCreation = section.querySelector('[data-action="cancel-created-date"]');
    if (cancelCreation) { cancelCreation.addEventListener('click', function () {
      if (pendingCreationDate === creationRequest) { pendingCreationDate = null; Store.touch(); }
    }); }

    var selectAllRde = section.querySelector('[data-action="select-all-rde"]');
    if (selectAllRde) {
      selectAllRde.addEventListener('click', function () {
        var project = Store.getState().project, importInfo = Store.getState().importState.gl;
        toggleAllRdeFields(true);
        if (global.JetFocus) { global.JetFocus.defer(function () {
          if (Store.getState().project !== project || Store.getState().importState.gl !== importInfo) { return null; }
          return document.querySelector('[data-bind="mapping-gl"] [data-rde-column]');
        }); }
      });
    }

    var clearAllRde = section.querySelector('[data-action="clear-all-rde"]');
    if (clearAllRde) {
      clearAllRde.addEventListener('click', function () {
        toggleAllRdeFields(false);
        if (global.JetFocus) { global.JetFocus.defer(function () {
          return document.querySelector('[data-action="select-all-rde"]');
        }); }
      });
    }

    section.querySelectorAll('[data-rde-label]').forEach(function (input) {
      input.addEventListener('input', function () {
        var column = input.getAttribute('data-rde-label');
        keepEditing('gl');
        Store.patchGlMappingOptionsQuiet({
          rdeFields: (glOptions().rdeFields || []).map(function (field) {
            return field.sourceColumn === column
              ? Object.assign({}, field, { label: input.value })
              : field;
          })
        });
        // 名稱是否空白會改變「確認配對」可用性與待補提示；就地更新，失焦時不重建整步。
        // 舊做法在 blur 後延遲重建，滑鼠直接點確認鈕時按鈕會在按下與放開之間被換掉（2026-10-02 W3）。
        refreshMappingDerived(section, 'gl', Ui.GL_FIELDS);
      });
    });

    section.querySelectorAll('[data-rde-type]').forEach(function (select) {
      select.addEventListener('change', function () {
        var column = select.getAttribute('data-rde-type');
        Store.patchGlMappingOptions({
          rdeFields: (glOptions().rdeFields || []).map(function (field) {
            return field.sourceColumn === column
              ? Object.assign({}, field, { valueType: select.value })
              : field;
          })
        });
      });
    });
  }

  // 使用者正在文字輸入框修改已確認的配對時，維持編輯畫面；之後的背景重建不收回成摘要卡，
  // 也就不會把正在輸入的欄位換掉。完成確認或取消變更時照舊收回。
  function keepEditing(kind) {
    if (Store.getState().mapping[kind].committed) { editing[kind] = true; }
  }

  // 文字輸入的就地更新：只換掉由草稿推導的必填清單、狀態提示與待補提示，並調整「確認配對」
  // 是否可按。確認鈕與輸入框本身都保留原節點，滑鼠點擊與輸入焦點不受影響。
  function refreshMappingDerived(section, kind, fields) {
    if (kind === 'gl') { ensureDcCodeComparisons(); }
    var mappingState = Store.getState().mapping[kind];
    var mode = kind === 'gl' ? mappingState.amountMode : mappingState.changeMode;
    var eligibility = mappingCommitEligibility(fields, mode, mappingState, kind);
    var committed = mappingState.committed;
    var matches = draftMatchesCommitted(mappingState, mode);

    var commit = section.querySelector('[data-action="commit-' + kind + '"]');
    if (commit) { commit.disabled = !eligibility.canCommit; }

    // 提示就地出現或消失時也走 JetMotion：出現的長出來、消失的收回去，和整段重繪時一樣。
    function update() {
      var rail = Ui.findLive(section, '.map-rail');
      if (rail) { rail.outerHTML = requiredRailHtml(eligibility, mappingState, kind); }

      replaceDerivedNotice(section, '[data-bind="mapping-banner-' + kind + '"]',
        mappingBannerHtml(kind, mappingState, committed, matches), function (html) {
          var toolbar = Ui.findLive(section, '.mapping-section__toolbar');
          if (toolbar) { toolbar.insertAdjacentHTML('afterend', html); }
        });
      replaceDerivedNotice(section, '[data-bind="mapping-option-problems"]', optionProblemsHtml(eligibility, kind),
        function (html) {
          var anchor = Ui.findLive(section, '[data-bind="mapping-commit-error-' + kind + '"]') ||
            Ui.findLive(section, '.panel__actions');
          if (anchor) { anchor.insertAdjacentHTML('beforebegin', html); }
        });
    }
    if (global.JetMotion && global.JetMotion.morph) { global.JetMotion.morph(section, update); } else { update(); }
  }

  function replaceDerivedNotice(section, selector, html, insert) {
    var existing = Ui.findLive(section, selector);
    if (existing) {
      if (html) { existing.outerHTML = html; } else { existing.remove(); }
    } else if (html) {
      insert(html);
    }
  }

  // 收回摘要卡時焦點交回「重新配對」。已換到別的步驟時畫面上找不到它，焦點就不動。
  function focusMapping(selector) {
    if (global.JetFocus && global.JetFocus.deferFirst) { global.JetFocus.deferFirst([selector]); }
  }

  function bindMappingSection(container, kind, fields) {
    var section = container.querySelector('[data-bind="mapping-' + kind + '"]');
    if (!section) { return; }

    // 設定修改後保留各組錯誤與導航，只標明需要重新確認；不把上一份錯誤冒充目前設定的結果。
    section.addEventListener('change', function () {
      markMappingCommitErrorModified(kind, section);
    }, true);
    section.addEventListener('input', function () {
      markMappingCommitErrorModified(kind, section);
    }, true);
    section.addEventListener('click', function (event) {
      if (event.target.closest('[data-action="add-code"], ' +
          '[data-action="select-all-rde"], [data-action="clear-all-rde"]')) {
        markMappingCommitErrorModified(kind, section);
      }
    }, true);

    if (kind === 'gl' && section.querySelector('[data-bind="gl-options"]')) {
      bindGlOptions(section);
    }

    // 必填清單會在文字輸入時就地換新（refreshMappingDerived），所以用委派綁在 section 上。
    // 配對失敗訊息的「前往設定」也走這裡：data-focus-mapping-field 對到欄位的來源欄選單，
    // data-focus-mapping-option 對到人工/自動判定方式或攸關資料元素型別（以 data-focus-key 找）。
    section.addEventListener('click', function (event) {
      var button = event.target && event.target.closest
        ? (event.target.closest('[data-focus-mapping-field]') || event.target.closest('[data-focus-mapping-option]'))
        : null;
      if (!button || !section.contains(button)) { return; }
      var key = button.getAttribute('data-focus-mapping-field');
      var focusKey = button.getAttribute('data-focus-mapping-option');
      var project = Store.getState().project;
      var stepIndex = Store.getState().currentStepIndex;
      if (Store.getState().mappingUiMode !== 'classic') { Store.setMappingUiMode('classic'); }
      global.setTimeout(function () {
        var state = Store.getState();
        if (state.project !== project || state.currentStepIndex !== stepIndex || state.view !== 'workflow') { return; }
        // 切換編輯畫面會替換整個步驟 body；必須從目前頁面取新節點，不對已卸下的容器定位。
        var content = Ui.$('content');
        var current = content && content.querySelector('.stepflow-item--current [data-bind="mapping-' + kind + '"]');
        if (!current) { return; }
        var attribute = key ? 'data-mapping-key' : 'data-focus-key';
        var wanted = key || focusKey;
        var target = Array.prototype.find.call(current.querySelectorAll('[' + attribute + ']'), function (control) {
          return control.getAttribute(attribute) === wanted;
        });
        if (target) { target.focus(); target.scrollIntoView({ block: 'center', inline: 'nearest' }); }
      }, 0);
    });

    // 摘要卡動作：重新配對（展開編輯）、預覽標準化資料。
    // 摘要卡和編輯畫面互換，按下的那一個跟著不見：展開時焦點交給編輯畫面的第一個控制項（使用者 2026-10-13 交接）。
    var remapBtn = section.querySelector('[data-action="remap-' + kind + '"]');
    if (remapBtn) {
      remapBtn.addEventListener('click', function () {
        editing[kind] = true;
        Store.touch();
        focusMapping('[data-bind="mapping-' + kind + '"] :is(input, select, textarea, button)');
      });
    }

    var previewTargetBtn = section.querySelector('[data-action="preview-target-' + kind + '"]');
    if (previewTargetBtn && Ui.openDataPreview) {
      previewTargetBtn.addEventListener('click', function () {
        Ui.openDataPreview(kind === 'gl' ? 'gl' : 'tb');
      });
    }

    // 標頭下拉：指派來源欄 → JET 欄位（維持一對一；字面值欄不視為欄位指派）。
    // bump 全步重繪後的捲動與焦點由 renderContent 依 data-preserve-scroll 與焦點識別屬性統一還原。
    var literalKeys = fields.filter(function (f) { return f.literal; }).map(function (f) { return f.key; });
    section.querySelectorAll('[data-map-col]').forEach(function (sel) {
      sel.addEventListener('change', function () {
        Store.assignColumnToField(kind, sel.getAttribute('data-map-col'), sel.value, literalKeys);
      });
    });

    // 模式切換：清不適用欄位指派後設模式。
    section.querySelectorAll('input[name="mode-' + kind + '"]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        changeMode(kind, fields, radio.value);
      });
    });

    // classic 的欄位下拉：寫入 draft。setMappingDraft 依通知慣例 bump 全步重繪，必填鐵軌、
    // 「確認配對」可用性、偏離橫幅與 GL 政策區同步更新；焦點與捲動由 renderContent 框架層還原。
    section.querySelectorAll('select[data-mapping-key]').forEach(function (control) {
      // 傳票核准日的下拉值帶前綴，由 bindGlOptions 另外處理，不能直接寫進草稿。
      if (control.hasAttribute('data-approval-choice')) { return; }
      control.addEventListener('change', function () {
        Store.setMappingDraft(kind, control.getAttribute('data-mapping-key'), control.value.trim());
      });
    });

    // 字面值輸入（grid/classic 共用的借方代碼）：每次輸入就寫入草稿，並就地更新受影響的提示與按鈕，
    // 失焦時不重建整步。滑鼠按下「確認配對」會先讓輸入框失焦；若此時重建，按鈕會在按下與放開之間
    // 被換掉，瀏覽器不產生 click，第一次點擊就沒有送出（2026-10-02 W3）。
    section.querySelectorAll('input[data-mapping-key]').forEach(function (control) {
      control.addEventListener('input', function () {
        keepEditing(kind);
        Store.setMappingLiteralQuiet(kind, control.getAttribute('data-mapping-key'), control.value.trim());
        refreshMappingDerived(section, kind, fields);
      });
      control.addEventListener('change', function () {
        // 只把畫面上的值整理成草稿實際保存的樣子；不換節點，焦點與按鈕都不受影響。
        var trimmed = control.value.trim();
        if (control.value !== trimmed) { control.value = trimmed; }
      });
    });

    // 取消變更：草稿與模式回到快照，收回摘要卡。
    var restoreBtn = section.querySelector('[data-action="restore-' + kind + '"]');
    if (restoreBtn) {
      restoreBtn.addEventListener('click', function () {
        mappingCommitErrors[kind] = null;
        if (kind === 'gl') { pendingCodeInputs = null; codeComparisonScope = null; }
        var committed = Store.getState().mapping[kind].committed;
        if (!committed || !committed.mapping) { return; }
        editing[kind] = false;
        Store.restoreCommittedMapping(kind);
        focusMapping('[data-action="remap-' + kind + '"]');
      });
    }

    // 草稿態（或已提交摘要卡）有二維表 → 惰性載入 PBC-GL 或 PBC-TB。
    // 捲動位置由 renderContent 依 data-preserve-scroll 統一還原，此處不再持有本地暫存。
    var gridWrap = section.querySelector('.map-grid-wrap');
    if (gridWrap) {
      ensureSourcePreview(kind, Store.getState().importState[kind]);
      [['[data-source-previous]', -1], ['[data-source-next]', 1], ['[data-source-retry]', 0]].forEach(function (item) {
        var button = section.querySelector(item[0]);
        if (button) { button.addEventListener('click', function () {
          var cache = sourceCache[kind];
          if (!cache || cache.loading) { return; }
          ensureSourcePreview(kind, Store.getState().importState[kind], item[1] === 0 ? cache.retryPage : Math.max(1, (cache.page || 1) + item[1]));
          Store.touch();
        }); }
      });
    }

    // 簡易清單的「預覽」：開資料預覽看 PBC-GL 或 PBC-TB（grid 模式無此鈕）。
    var previewSourceBtn = section.querySelector('[data-action="preview-source-' + kind + '"]');
    if (previewSourceBtn && Ui.openDataPreview) {
      previewSourceBtn.addEventListener('click', function () {
        Ui.openDataPreview(kind === 'gl' ? 'pbcGl' : 'pbcTb');
      });
    }

    var commitBtn = section.querySelector('[data-action="commit-' + kind + '"]');

    if (commitBtn) {
      commitBtn.addEventListener('click', function () {
        // 確認配對會清除第四步已儲存和未儲存的科目分類；還有沒儲存的分類時先問，沒有時照舊直接確認。
        if (!Ui.pendingAccountCategoryCount()) { commitMapping(); return; }
        Ui.confirmPendingAccountCategories('確認配對後，第四步已儲存和未儲存的科目分類都會清除。', '繼續確認', { saveable: false })
          .then(function (proceed) { if (proceed) { commitMapping(); } });
      });
    }

    function commitMapping() {
      mappingCommitErrors[kind] = null;
      var current = Store.getState().mapping[kind];
      var project = Store.getState().project;
      var importInfo = Store.getState().importState[kind];
      var mapping = {};
      Object.keys(current.draft).forEach(function (key) {
        if (current.draft[key]) { mapping[key] = current.draft[key]; }
      });

      var label = kind === 'gl' ? 'GL' : 'TB';
      var mode = kind === 'gl' ? current.amountMode : current.changeMode;
      function isCurrentSource() {
        var latest = Store.getState();
        return latest.project === project && latest.importState[kind] === importInfo;
      }

      Ui.run('確認 ' + label + ' 欄位配對', function () {
        var promise = kind === 'gl'
          ? global.JetApi.mappingCommitGl(glCommitPayload(mapping, mode))
          : global.JetApi.mappingCommitTb({ mapping: mapping, changeMode: mode });

        return promise.then(function (data) {
          if (!isCurrentSource()) { return; }
          mappingCommitErrors[kind] = null;
          editing[kind] = false;
          if (kind === 'gl') { pendingCodeInputs = null; }
          if (kind === 'gl') {
            // 後端回傳全部 canonical options 與 stable ID：草稿一律以 response 取代，
            // 讓下一次 recommit 沿用同一組欄位身分，不由畫面自行 mint。
            Store.replaceGlMappingOptions(canonicalGlOptions(data));
          }
          Store.setMappingCommitted(kind, {
            projectedRowCount: data.projectedRowCount,
            committedUtc: new Date().toISOString(),
            mapping: mapping,
            mode: mode,
            options: kind === 'gl' ? canonicalGlOptions(data) : null
          });
          if (kind === 'gl' && Object.prototype.hasOwnProperty.call(data, 'authorizedPreparerState')) {
            Store.refreshAuthorizedPreparerState(data.authorizedPreparerState);
          }
          Store.applyMutationEffects(data);
          // 後端非阻斷提醒（如必填欄整欄空白、疑似配錯欄）：逐則以警示色呈現，使用者立即看得到。
          (data.warnings || []).forEach(function (w) {
            Store.addMessage(label + ' 配對提醒：' + w, 'warn');
          });
          focusMapping('[data-action="remap-' + kind + '"]');
          return data;
        }).catch(function (error) {
          if (!isCurrentSource()) { return; }
          throw error;
        });
      }, {
        // 完成句由 Ui.run 接上耗時（共用的秒與分秒寫法）後記一則（使用者 2026-10-13 裁定）。
        completionMessage: function (data) {
          return data ? label + ' 已確認配對，共 ' + Number(data.projectedRowCount).toLocaleString() + ' 筆。' : '';
        },
        // 原因寫在配對區的錯誤框，不另外在步驟上方顯示。
        inlineError: true,
        onError: function (_, error) {
          mappingCommitErrors[kind] = {
            project: project,
            importInfo: importInfo,
            // 後端沒有附原因時（使用者 2026-10-13 勾選頁確認 M1）。
            message: Ui.errorText(error, '確認配對未完成，請再試一次。'),
            details: error && Array.isArray(error.details) ? error.details : null
          };
          Store.touch();
        }
      });
    }
  }

  Ui.registerStep('mapping', render);
})(window);
