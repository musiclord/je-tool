/*
  Step 2：欄位配對。
  來源欄位 → JET 邏輯欄位的對應；自動建議與提交標準化都在後端執行。

  每個資料集（GL/TB）的狀態模型（消除「已提交綠字 + 仍可按確認」的語意衝突）：
    未匯入        → 警示（請先匯入資料）
    草稿          → 編輯表格 + 自動建議/預覽來源資料/確認配對
    已提交        → 收合摘要卡（模式、標準化列數、提交時間、key→欄名清單）；
                    只有「重新配對」「預覽標準化資料」兩個動作，編輯表格不渲染
    草稿偏離      → 編輯表格 + 「修改尚未生效」橫幅 + 重新確認配對/還原為已提交版本
    來源變更失效  → 草稿 + 「來源資料已變更，原配對已失效」橫幅
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  // 「重新配對」的本地旗標：已提交且草稿未偏離時仍顯示編輯畫面。
  var editing = { gl: false, tb: false };

  // 來源原貌預覽快取（每資料集一份，依 batchId 失效）。
  // 形狀：null | { batchId, loading:true } | { batchId, error:true } | { batchId, columns, rows, totalCount }
  var sourceCache = { gl: null, tb: null };
  var sourceResponseGuards = {
    gl: Ui.createLatestResponseGuard(),
    tb: Ui.createLatestResponseGuard()
  };

  // 對照表格橫向捲動的暫存位置：指派欄位觸發全步重繪前捕捉、重繪後還原（見 bindMappingSection）。
  // null = 本輪重繪非由欄位指派引發（例如切模式），不還原、讓捲動自然歸零。
  var pendingGridScroll = { gl: null, tb: null };

  // 來源欄值分布快取（mapping.valueProfile）：以「批次 + 來源欄」為鍵。
  // 形狀：{ loading:true } | { error:true } | { blankCount, distinctCount, values, truncated }
  // 這是後端 set-based 聚合的有界結果；前端不掃描來源列、不自行統計。
  var valueProfileCache = {};
  var valueProfileGuard = Ui.createLatestResponseGuard();

  Ui.registerWorkflowReset(function () {
    sourceResponseGuards.gl.invalidate();
    sourceResponseGuards.tb.invalidate();
    valueProfileGuard.invalidate();
    editing = { gl: false, tb: false };
    sourceCache = { gl: null, tb: null };
    pendingGridScroll = { gl: null, tb: null };
    valueProfileCache = {};
  });

  function glOptions() {
    return Store.getState().mapping.gl.options;
  }

  // 目前模式下不得由來源欄指派的 JET 欄位：核准日設為「與總帳日期相同」時，
  // 傳票核准日與衍生模式互斥（manifest：sameAsPostDate 與 docDate 配對互斥）。
  function excludedFieldKeys(kind) {
    if (kind !== 'gl') { return []; }
    return glOptions().approvalDateMode === 'sameAsPostDate' ? ['docDate'] : [];
  }

  function profileKey(kind, column) {
    var importInfo = Store.getState().importState[kind];
    return (importInfo ? importInfo.batchId : 'none') + '|' + column;
  }

  // 惰性取得來源欄值分布。只發 mapping.valueProfile，載入完成後 touch() 重繪；
  // 值、次數、空白數與是否截斷全部由後端決定，前端不補算 distinct。
  function ensureValueProfile(kind, column) {
    if (!column) { return; }
    var key = profileKey(kind, column);
    if (valueProfileCache[key]) { return; }
    var requestState = Store.getState();
    var projectId = requestState.project ? requestState.project.projectId : null;
    var accept = valueProfileGuard.issue(function () {
      var latest = Store.getState();
      return !!latest.project && latest.project.projectId === projectId;
    });
    valueProfileCache[key] = { loading: true };
    Store.touch();
    global.JetApi.mappingValueProfile({
      dataset: 'gl',
      sourceColumn: column,
      limit: Ui.VALUE_PROFILE_LIMIT
    }).then(function (data) {
      if (!accept()) { return; }
      valueProfileCache[key] = {
        blankCount: data.blankCount,
        distinctCount: data.distinctCount,
        values: data.values || [],
        truncated: !!data.truncated
      };
      Store.touch();
    }).catch(function () {
      if (!accept()) { return; }
      valueProfileCache[key] = { error: true };
      Store.touch();
    });
  }

  // 當前模式下可由「來源欄」指派的 JET 欄位（排除字面值欄與目前政策互斥的欄位）。
  function assignableFields(fields, mode, excluded) {
    var skip = excluded || [];
    return fields.filter(function (f) {
      if (f.literal) { return false; }
      if (skip.indexOf(f.key) >= 0) { return false; }
      if (f.req === 'always' || f.req === 'optional') { return true; }
      return Array.isArray(f.req) && f.req.indexOf(mode) >= 0;
    });
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

  // 當前模式適用的字面值欄（GL side/flag 的 dcDebitCode；TB 無，回 null）。
  function literalFieldFor(fields, mode) {
    return fields.filter(function (f) {
      return f.literal && Array.isArray(f.req) && f.req.indexOf(mode) >= 0;
    })[0] || null;
  }

  // 當前模式下尚未指派的必填欄位（含字面值必填欄）。
  function missingRequired(fields, mode, mappingState) {
    return fields.filter(function (f) {
      return Ui.isRequired(f, mode) && !mappingState.draft[f.key];
    });
  }

  // 切模式：清掉不適用新模式的金額類欄位指派（否則標頭下拉顯示不到的殘留會留在 draft），再設模式。
  function changeMode(kind, fields, mode) {
    var draft = Store.getState().mapping[kind].draft;
    var pruned = {};
    Object.keys(draft).forEach(function (k) {
      var f = fields.filter(function (x) { return x.key === k; })[0];
      if (!f) { return; }
      if (Array.isArray(f.req) && f.req.indexOf(mode) < 0) { return; }
      pruned[k] = draft[k];
    });
    Store.replaceMappingDraft(kind, pruned);
    Store.setMappingMode(kind, mode);
  }

  // 惰性載入來源原貌前 10 列；載入完成後 touch() 重建面板顯示資料。
  function ensureSourcePreview(kind, importInfo) {
    if (!importInfo) { return; }
    var c = sourceCache[kind];
    if (c && c.batchId === importInfo.batchId && (c.loading || c.rows || c.error)) { return; }
    var requestState = Store.getState();
    var projectId = requestState.project ? requestState.project.projectId : null;
    var batchId = importInfo.batchId;
    var acceptResponse = sourceResponseGuards[kind].issue(function () {
      var latest = Store.getState();
      var latestImport = latest.importState[kind];
      return !!latest.project && latest.project.projectId === projectId &&
        !!latestImport && latestImport.batchId === batchId;
    });
    sourceCache[kind] = { batchId: importInfo.batchId, loading: true };
    global.JetApi.queryDataPreview({ dataset: kind === 'gl' ? 'glStaging' : 'tbStaging', limit: 10 })
      .then(function (data) {
        if (!acceptResponse()) { return; }
        sourceCache[kind] = {
          batchId: batchId,
          columns: data.columns || [],
          rows: data.rows || [],
          totalCount: data.totalCount || 0
        };
        Store.touch();
      })
      .catch(function () {
        if (!acceptResponse()) { return; }
        sourceCache[kind] = { batchId: batchId, error: true };
        Store.touch();
      });
  }

  // 配對介面切換（簡易清單／對照表格）。渲染在 GL 與 TB「各自的」編輯區頂部，
  // 讓使用者在任一資料集就地切換、不必捲回頁面最上方。兩處共用同一份 session 級
  // mappingUiMode（bump 後全步重繪，兩處 is-active 一致），只是入口就近可達。
  // 已提交的摘要卡是唯讀、無可切換的編輯介面，故該狀態不渲染此切換。
  function uiModeToggleHtml() {
    var uiMode = Store.getState().mappingUiMode;
    function btn(value, label) {
      return '<button type="button" class="seg-toggle__btn' + (uiMode === value ? ' is-active' : '') +
        '" data-ui-mode="' + value + '">' + label + '</button>';
    }
    return '<div class="seg-toggle" role="group" aria-label="配對介面">' +
      '<span class="seg-toggle__label">配對介面</span>' +
      btn('classic', '簡易清單') + btn('grid', '對照表格') +
      '</div>';
  }

  function bindUiModeToggle(container) {
    container.querySelectorAll('[data-ui-mode]').forEach(function (b) {
      b.addEventListener('click', function () {
        Store.setMappingUiMode(b.getAttribute('data-ui-mode'));
      });
    });
  }

  function render(container, state) {
    if (!state.project) {
      container.innerHTML = Ui.noProjectPanel('欄位配對');
      Ui.bindNoProjectPanel(container);
      return;
    }

    var canRestore = !!state.importState.gl && !!state.importState.tb;
    container.innerHTML =
      '<div class="panel panel--wide panel--mapping">' +
        '<h2 class="panel__title">欄位配對</h2>' +
        '<p class="panel__hint">將來源欄位對應到 JET 邏輯欄位。可先自動建議再人工確認；標 * 者為目前模式的必填欄位。</p>' +
        Ui.mappingReviewBannerHtml(state) +
        '<div class="panel__actions">' +
          '<button type="button" class="btn btn--ghost" data-action="restore-mapping-draft"' +
            (canRestore ? '' : ' disabled title="請先匯入 GL 與 TB"') +
            '>從既有報告載入配對草稿</button>' +
        '</div>' +
        mappingSection('gl', 'GL 欄位配對', Ui.GL_FIELDS, Ui.GL_MODES,
          state.importState.gl, state.mapping.gl, state.mapping.gl.amountMode) +
        mappingSection('tb', 'TB 欄位配對', Ui.TB_FIELDS, Ui.TB_MODES,
          state.importState.tb, state.mapping.tb, state.mapping.tb.changeMode) +
        Ui.stepFooterHtml(state) +
      '</div>';

    bindUiModeToggle(container);
    bindRestoreMappingDraft(container);
    bindMappingSection(container, 'gl', Ui.GL_FIELDS);
    bindMappingSection(container, 'tb', Ui.TB_FIELDS);
    Ui.bindStepFooter(container);
  }

  function bindRestoreMappingDraft(container) {
    var button = container.querySelector('[data-action="restore-mapping-draft"]');
    if (!button) { return; }

    button.addEventListener('click', function () {
      Ui.run('載入配對草稿', function () {
        return global.JetApi.hostSelectFile({
          title: '選擇 JET ValidationReport 或 WorkingPaper',
          extensions: ['.xlsx']
        }).then(function (file) {
          if (!file.filePath) { return; }
          return global.JetApi.mappingRestoreDraft({ filePath: file.filePath }).then(function (data) {
            editing.gl = true;
            editing.tb = true;
            Store.restoreMappingDrafts(data);
            Store.addMessage('已載入 GL／TB 配對草稿；請檢查後分別確認配對。', 'info');
          });
        });
      });
    });
  }

  // 簡易清單與對照表格共用同一份提交資格鏡像；後端 mapping.commit 仍是權威驗證。
  // GL 另檢查標準化政策草稿（核准日互斥、過帳狀態政策、人工／自動代碼、攸關資料元素欄位）；
  // 這些只是就近引導，值的正規化、去重與跨欄裁定仍全在系統端。
  function mappingCommitEligibility(fields, mode, mappingState, kind) {
    var required = fields.filter(function (field) { return Ui.isRequired(field, mode); });
    var missing = missingRequired(fields, mode, mappingState);
    var problems = kind === 'gl' ? glOptionProblems(mappingState) : [];
    return {
      required: required,
      missing: missing,
      problems: problems,
      canCommit: missing.length === 0 && problems.length === 0
    };
  }

  // GL 標準化政策的前端引導檢查。每條都對應 manifest mapping.commit.gl 的既有規則，
  // 不新增語意：核准日三態互斥、過帳狀態政策至少一個接受值或含空白、人工／自動兩組
  // 各至少一值且不得重複、已勾選的攸關資料元素欄位需要顯示名稱。
  function glOptionProblems(mappingState) {
    var options = glOptions();
    var draft = mappingState.draft;
    var problems = [];

    if (options.approvalDateMode === 'mapped' && !draft.docDate) {
      problems.push('核准日選「由來源欄提供」時，必須指派「傳票核准日」來源欄');
    }
    if (options.approvalDateMode === 'sameAsPostDate' && draft.docDate) {
      problems.push('核准日選「與總帳日期相同」時，不可同時指派「傳票核准日」來源欄');
    }

    if (draft.postingStatus) {
      var policy = options.postingStatusPolicy;
      var accepted = policy && Array.isArray(policy.acceptedValues) ? policy.acceptedValues : [];
      if (accepted.length === 0 && !(policy && policy.includeBlank)) {
        problems.push('已指派過帳狀態來源欄時，必須選至少一個代表「已過帳」的值，或勾選接受空白');
      }
    }

    var manual = options.manualAutoPolicy || { manualValues: [], automaticValues: [] };
    if (!manual.manualValues.length || !manual.automaticValues.length) {
      problems.push('人工與自動代碼各需至少一個值');
    } else if (manual.manualValues.some(function (value) {
      return manual.automaticValues.some(function (other) {
        return normalizedCode(value) === normalizedCode(other);
      });
    })) {
      problems.push('人工與自動代碼不得重複');
    }

    if ((options.rdeFields || []).some(function (field) {
      return !field.label || !field.label.trim();
    })) {
      problems.push('已勾選的攸關資料元素欄位都需要顯示名稱');
    }

    return problems;
  }

  // 代碼比較的就近鏡像：系統端一律 trim 後不分大小寫比對；畫面只用它做「是否重複」提示。
  function normalizedCode(value) {
    return String(value == null ? '' : value).trim().toUpperCase();
  }

  /* ---- GL 標準化政策編輯器 ---------------------------------------------------- */

  function glOptionsHtml(importInfo, mappingState) {
    var options = glOptions();
    var draft = mappingState.draft;
    return (
      '<section class="map-options" data-bind="gl-options">' +
        '<h4 class="map-options__title">標準化政策</h4>' +
        '<p class="map-options__hint">這些設定決定哪些分錄進入測試母體，以及要保留哪些額外欄位。' +
          '所有判定都由系統執行，畫面只收集設定。</p>' +
        approvalModeHtml(options, draft) +
        postingStatusPolicyHtml(options, draft) +
        manualAutoPolicyHtml(options, draft) +
        rdeFieldsHtml(options, draft, importInfo) +
      '</section>'
    );
  }

  function approvalModeHtml(options, draft) {
    var radios = Ui.GL_APPROVAL_DATE_MODES.map(function (m) {
      return '<label class="mode-option">' +
        '<input type="radio" name="gl-approval-mode" value="' + m.value + '"' +
          (options.approvalDateMode === m.value ? ' checked' : '') +
          ' data-option-bind="approvalDateMode" aria-describedby="gl-approval-help">' +
        '<span>' + m.label + '</span></label>';
    }).join('');

    var note = options.approvalDateMode === 'mapped'
      ? (draft.docDate
          ? '目前以來源欄「' + Ui.esc(draft.docDate) + '」作為核准日。'
          : '請在上方把「傳票核准日」指派到一個來源欄。')
      : (options.approvalDateMode === 'sameAsPostDate'
          ? '每列的核准日直接沿用標準化後的總帳日期；此設定下不可再指派「傳票核准日」來源欄。'
          : '這份總帳沒有核准日；需要核准日的測試會標示為無法執行。');

    return '<fieldset class="map-options__group">' +
      '<legend class="map-options__legend">核准日</legend>' +
      '<div class="mode-group">' + radios + '</div>' +
      '<p class="map-options__note" id="gl-approval-help">' + note + '</p>' +
      '</fieldset>';
  }

  function postingStatusPolicyHtml(options, draft) {
    if (!draft.postingStatus) {
      return '<fieldset class="map-options__group">' +
        '<legend class="map-options__legend">過帳狀態</legend>' +
        '<p class="map-options__note">尚未指派「過帳狀態」來源欄；不指派時不套用狀態排除，' +
          '測試母體只以查核期間界定。</p>' +
        '</fieldset>';
    }

    var policy = options.postingStatusPolicy || { acceptedValues: [], includeBlank: false };
    var accepted = policy.acceptedValues || [];
    var profile = valueProfileCache[profileKey('gl', draft.postingStatus)];
    var body;
    if (!profile) {
      body = '<button type="button" class="btn btn--ghost btn--tiny" data-action="load-posting-profile">' +
        '讀取來源值</button>';
    } else if (profile.loading) {
      body = '<p class="map-options__note">讀取來源值中…</p>';
    } else if (profile.error) {
      body = '<p class="map-options__note">讀取來源值失敗。' +
        '<button type="button" class="btn btn--ghost btn--tiny" data-action="load-posting-profile">' +
        '再試一次</button></p>';
    } else {
      var checks = profile.values.map(function (item, index) {
        var checked = accepted.some(function (value) {
          return normalizedCode(value) === normalizedCode(item.value);
        });
        return '<label class="value-option">' +
          '<input type="checkbox" data-posting-value="' + Ui.esc(item.value) + '"' +
            (checked ? ' checked' : '') + ' id="posting-value-' + index + '">' +
          '<span class="value-option__text">' + Ui.esc(item.value) + '</span>' +
          '<span class="value-option__count">' + Number(item.count).toLocaleString() + ' 列</span>' +
          '</label>';
      }).join('');
      body =
        '<p class="map-options__note">來源欄「' + Ui.esc(draft.postingStatus) + '」共 ' +
          Number(profile.distinctCount).toLocaleString() + ' 種值、空白 ' +
          Number(profile.blankCount).toLocaleString() + ' 列。勾選代表「已過帳」的值。' +
          (profile.truncated ? '值太多，只列出最常出現的幾種；其餘可用下方欄位補上。' : '') +
        '</p>' +
        '<div class="value-option-list">' + checks + '</div>' +
        (profile.truncated
          ? '<div class="map-options__inline">' +
              '<label class="map-options__inline-label" for="posting-value-add">補充其他值</label>' +
              '<input class="form__input form__input--tiny" type="text" id="posting-value-add"' +
                ' data-bind="posting-value-add">' +
              '<button type="button" class="btn btn--ghost btn--tiny" data-action="add-posting-value">' +
                '加入</button>' +
            '</div>'
          : '');
    }

    var selected = accepted.length
      ? '<p class="map-options__note">目前接受：' +
          accepted.map(function (value) {
            return '<span class="value-chip">' + Ui.esc(value) +
              '<button type="button" class="value-chip__remove" data-remove-posting-value="' +
              Ui.esc(value) + '" aria-label="移除 ' + Ui.esc(value) + '">×</button></span>';
          }).join('') + '</p>'
      : '';

    return '<fieldset class="map-options__group">' +
      '<legend class="map-options__legend">過帳狀態</legend>' +
      body +
      selected +
      '<label class="value-option">' +
        '<input type="checkbox" data-option-bind="includeBlank"' +
          (policy.includeBlank ? ' checked' : '') + '>' +
        '<span class="value-option__text">空白也視為已過帳</span>' +
      '</label>' +
      '</fieldset>';
  }

  function codeChipsHtml(values, group) {
    if (!values.length) {
      return '<span class="map-options__note">尚未設定</span>';
    }
    return values.map(function (value) {
      return '<span class="value-chip">' + Ui.esc(value) +
        '<button type="button" class="value-chip__remove" data-remove-code="' + Ui.esc(value) +
        '" data-code-group="' + group + '" aria-label="移除 ' + Ui.esc(value) + '">×</button></span>';
    }).join('');
  }

  function manualAutoPolicyHtml(options, draft) {
    var policy = options.manualAutoPolicy;
    if (!draft.manual) {
      return '<fieldset class="map-options__group">' +
        '<legend class="map-options__legend">人工／自動分錄代碼</legend>' +
        '<p class="map-options__note">尚未指派「人工/自動分錄」來源欄；不指派時不判定人工或自動，' +
          '需要這項的測試會標示為無法執行。</p>' +
        '</fieldset>';
    }

    var profile = valueProfileCache[profileKey('gl', draft.manual)];
    var body;
    if (!profile) {
      body = '<button type="button" class="btn btn--ghost btn--tiny" data-action="load-manual-profile">' +
        '讀取來源值</button>';
    } else if (profile.loading) {
      body = '<p class="map-options__note">讀取來源值中…</p>';
    } else if (profile.error) {
      body = '<p class="map-options__note">讀取來源值失敗。' +
        '<button type="button" class="btn btn--ghost btn--tiny" data-action="load-manual-profile">' +
        '再試一次</button></p>';
    } else {
      var rows = profile.values.map(function (item, index) {
        var isManual = policy.manualValues.some(function (v) {
          return normalizedCode(v) === normalizedCode(item.value);
        });
        var isAutomatic = policy.automaticValues.some(function (v) {
          return normalizedCode(v) === normalizedCode(item.value);
        });
        var assigned = isManual ? 'manual' : (isAutomatic ? 'automatic' : 'none');
        function radio(value, label) {
          return '<label class="mode-option mode-option--tiny">' +
            '<input type="radio" name="manual-code-' + index + '" value="' + value + '"' +
              (assigned === value ? ' checked' : '') +
              ' data-manual-assign="' + Ui.esc(item.value) + '">' +
            '<span>' + label + '</span></label>';
        }
        return '<li class="value-assign">' +
          '<span class="value-assign__value">' + Ui.esc(item.value) + '</span>' +
          '<span class="value-assign__count">' + Number(item.count).toLocaleString() + ' 列</span>' +
          '<span class="value-assign__modes">' + radio('manual', '人工') + radio('automatic', '自動') +
            radio('none', '不歸類') + '</span>' +
          '</li>';
      }).join('');
      body =
        '<p class="map-options__note">來源欄「' + Ui.esc(draft.manual) + '」共 ' +
          Number(profile.distinctCount).toLocaleString() + ' 種值、空白 ' +
          Number(profile.blankCount).toLocaleString() + ' 列。' +
          '已指派這個欄位時，空白或未歸類的值會讓整批標準化失敗。' +
          (profile.truncated ? '值太多，只列出最常出現的幾種。' : '') +
        '</p>' +
        '<ul class="value-assign-list">' + rows + '</ul>';
    }

    return '<fieldset class="map-options__group">' +
      '<legend class="map-options__legend">人工／自動分錄代碼</legend>' +
      body +
      '<div class="map-options__codes">' +
        '<div class="map-options__code-group">' +
          '<span class="map-options__code-label">人工</span>' +
          codeChipsHtml(policy.manualValues, 'manual') +
          '<label class="visually-hidden" for="manual-code-add">新增人工代碼</label>' +
          '<input class="form__input form__input--tiny" type="text" id="manual-code-add"' +
            ' data-bind="manual-code-add" placeholder="新增值">' +
          '<button type="button" class="btn btn--ghost btn--tiny" data-action="add-code"' +
            ' data-code-group="manual">加入</button>' +
        '</div>' +
        '<div class="map-options__code-group">' +
          '<span class="map-options__code-label">自動</span>' +
          codeChipsHtml(policy.automaticValues, 'automatic') +
          '<label class="visually-hidden" for="automatic-code-add">新增自動代碼</label>' +
          '<input class="form__input form__input--tiny" type="text" id="automatic-code-add"' +
            ' data-bind="automatic-code-add" placeholder="新增值">' +
          '<button type="button" class="btn btn--ghost btn--tiny" data-action="add-code"' +
            ' data-code-group="automatic">加入</button>' +
        '</div>' +
      '</div>' +
      '</fieldset>';
  }

  // 只列出「沒有被核心欄位配對佔用」的來源欄；勾選後才需要顯示名稱與型別。
  // 型別解析、空白判定與失敗時的整批 rollback 都在系統端，畫面不猜型別。
  function rdeFieldsHtml(options, draft, importInfo) {
    var used = {};
    Object.keys(draft).forEach(function (key) {
      if (key !== 'dcDebitCode' && draft[key]) { used[draft[key]] = true; }
    });
    var selected = options.rdeFields || [];
    var columns = (importInfo.columns || []).filter(function (column) { return !used[column]; });

    var rows = columns.map(function (column, index) {
      var hit = selected.filter(function (f) { return f.sourceColumn === column; })[0];
      var controls = hit
        ? '<span class="rde-field__controls">' +
            '<label class="visually-hidden" for="rde-label-' + index + '">' +
              Ui.esc(column) + ' 的顯示名稱</label>' +
            '<input class="form__input form__input--tiny" type="text" id="rde-label-' + index + '"' +
              ' data-rde-label="' + Ui.esc(column) + '" value="' + Ui.esc(hit.label) +
              '" maxlength="' + Ui.RDE_MAX_LABEL_LENGTH + '">' +
            '<label class="visually-hidden" for="rde-type-' + index + '">' +
              Ui.esc(column) + ' 的資料型別</label>' +
            '<select id="rde-type-' + index + '" data-rde-type="' + Ui.esc(column) + '">' +
              Ui.RDE_VALUE_TYPES.map(function (t) {
                return '<option value="' + t.value + '"' +
                  (hit.valueType === t.value ? ' selected' : '') + '>' + t.label + '</option>';
              }).join('') +
            '</select>' +
          '</span>'
        : '';
      return '<li class="rde-field' + (hit ? ' is-selected' : '') + '">' +
        '<label class="value-option">' +
          '<input type="checkbox" data-rde-column="' + Ui.esc(column) + '"' + (hit ? ' checked' : '') + '>' +
          '<span class="value-option__text">' + Ui.esc(column) + '</span>' +
        '</label>' +
        controls +
        '</li>';
    }).join('');

    return '<fieldset class="map-options__group">' +
      '<legend class="map-options__legend">攸關資料元素欄位</legend>' +
      '<p class="map-options__note">勾選要一併保留的額外來源欄，它們可用於進階條件、抽樣測試與正式底稿。' +
        '沒有勾選的來源欄不會被保留。</p>' +
      (rows
        ? '<ul class="rde-field-list">' + rows + '</ul>'
        : '<p class="map-options__note">目前所有來源欄都已對應到 JET 欄位，沒有可額外保留的欄位。</p>') +
      '</fieldset>';
  }

  /* ---- 狀態判定 -------------------------------------------------------------- */

  // 草稿（非空值）與已提交快照逐鍵相等，且模式一致。
  function draftMatchesCommitted(mappingState, mode) {
    var committed = mappingState.committed;
    if (!committed || !committed.mapping) { return false; }
    if (committed.mode && committed.mode !== mode) { return false; }

    var draftKeys = Object.keys(mappingState.draft).filter(function (k) { return mappingState.draft[k]; });
    var committedKeys = Object.keys(committed.mapping).filter(function (k) { return committed.mapping[k]; });
    if (draftKeys.length !== committedKeys.length) { return false; }

    return draftKeys.every(function (k) { return mappingState.draft[k] === committed.mapping[k]; });
  }

  function modeLabel(modes, mode) {
    var hit = modes.filter(function (m) { return m.value === mode; })[0];
    return hit ? hit.label : mode;
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
      facts.push('已標準化 ' + Number(committed.projectedRowCount).toLocaleString() + ' 列');
    }
    if (committed.committedUtc) {
      facts.push('提交於 ' + new Date(committed.committedUtc).toLocaleString('zh-Hant', { hour12: false }));
    }

    return (
      '<section class="mapping-section" data-bind="mapping-' + kind + '">' +
        '<h3 class="mapping-section__title">' + title + '</h3>' +
        '<div class="mapping-summary">' +
          '<p class="mapping-summary__status">已提交，依此配對執行後續測試' +
            '<span class="mapping-summary__facts">' + facts.map(Ui.esc).join('・') + '</span></p>' +
          committedTableHtml(kind, fields, importInfo, committed) +
          literalNoteHtml(fields, committed) +
          committedOptionsHtml(kind, committed) +
          '<div class="panel__actions">' +
            '<button type="button" class="btn btn--ghost" data-action="remap-' + kind + '">重新配對</button>' +
            '<button type="button" class="btn btn--ghost" data-action="preview-target-' + kind +
              '" title="開啟資料預覽，檢視標準化後資料">預覽標準化資料</button>' +
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
    var facts = ['核准日：' + (approval ? approval.label : options.approvalDateMode)];

    if (options.postingStatusPolicy) {
      var accepted = options.postingStatusPolicy.acceptedValues || [];
      facts.push('過帳狀態接受 ' +
        (accepted.length ? accepted.join('、') : '（無）') +
        (options.postingStatusPolicy.includeBlank ? '，含空白' : ''));
    } else {
      facts.push('未套用過帳狀態排除');
    }

    var manual = options.manualAutoPolicy || { manualValues: [], automaticValues: [] };
    facts.push('人工 ' + manual.manualValues.join('、') + '／自動 ' + manual.automaticValues.join('、'));

    var rde = options.rdeFields || [];
    facts.push(rde.length
      ? '攸關資料元素欄位 ' + rde.length + ' 個：' + rde.map(function (f) { return f.label; }).join('、')
      : '未保留額外的攸關資料元素欄位');

    return '<p class="mapping-summary__options" data-bind="gl-committed-options">' +
      facts.map(Ui.esc).join('・') + '</p>';
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
    if (!cache || (importInfo && cache.batchId !== importInfo.batchId) || cache.loading) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">載入預覽中…</td></tr>';
    } else if (cache.error) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">預覽載入失敗，可重新進入此步驟再試。</td></tr>';
    } else if (!cache.rows.length) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">此批次尚無資料列。</td></tr>';
    } else {
      bodyHtml = cache.rows.map(function (row) {
        return '<tr>' + columns.map(function (col, i) {
          var cell = row[i];
          return '<td class="map-grid__data' + (cell == null ? ' is-null' : '') + '">' +
            (cell == null ? '∅' : Ui.esc(cell)) + '</td>';
        }).join('') + '</tr>';
      }).join('');
    }

    var note = '';
    if (cache && cache.rows && !cache.loading && !cache.error) {
      note = '<p class="map-grid__note">顯示前 ' + cache.rows.length + ' 列／共 ' +
        Number(cache.totalCount).toLocaleString() + ' 列</p>';
    }

    return '<div class="map-grid-wrap"><table class="map-grid">' +
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

  // 字面值欄(GL flag 的 dcDebitCode)不是來源欄,於表下補述其值。
  function literalNoteHtml(fields, committed) {
    var notes = fields.filter(function (f) {
      return f.literal && committed.mapping && committed.mapping[f.key];
    }).map(function (f) {
      return Ui.esc(f.label) + ' = ' + Ui.esc(committed.mapping[f.key]);
    });
    return notes.length
      ? '<p class="mapping-summary__literal">' + notes.join('・') + '</p>'
      : '';
  }

  function twoDimMappingTable(kind, fields, importInfo, mappingState, mode) {
    var columns = importInfo.columns || [];
    var assignable = assignableFields(fields, mode, excludedFieldKeys(kind));
    var span = Math.max(columns.length, 1);

    var assignRow = columns.map(function (col) {
      var assigned = fieldForColumn(fields, mappingState, col);
      var opts = '<option value="">（不對應）</option>' + assignable.map(function (f) {
        return '<option value="' + f.key + '"' + (f.key === assigned ? ' selected' : '') + '>' +
          Ui.esc(f.label) + '</option>';
      }).join('');
      return '<th class="map-grid__cell' + (assigned ? ' is-assigned' : '') + '">' +
        '<select class="map-grid__select" data-map-col="' + Ui.esc(col) + '">' + opts + '</select>' +
        '</th>';
    }).join('');

    var headRow = columns.map(function (col) {
      return '<th class="map-grid__colname">' + Ui.esc(col) + '</th>';
    }).join('');

    var cache = sourceCache[kind];
    var bodyHtml;
    if (!cache || cache.batchId !== importInfo.batchId || cache.loading) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">載入預覽中…</td></tr>';
    } else if (cache.error) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">預覽載入失敗，可重新進入此步驟再試。</td></tr>';
    } else if (!cache.rows.length) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">此批次尚無資料列。</td></tr>';
    } else {
      bodyHtml = cache.rows.map(function (row) {
        return '<tr>' + columns.map(function (col, i) {
          var cell = row[i];
          return '<td class="map-grid__data' + (cell == null ? ' is-null' : '') + '">' +
            (cell == null ? '∅' : Ui.esc(cell)) + '</td>';
        }).join('') + '</tr>';
      }).join('');
    }

    var note = '';
    if (cache && cache.rows && !cache.loading && !cache.error) {
      note = '<p class="map-grid__note">顯示前 ' + cache.rows.length + ' 列／共 ' +
        Number(cache.totalCount).toLocaleString() + ' 列</p>';
    }

    return '<div class="map-grid-wrap"><table class="map-grid">' +
      '<thead><tr class="map-grid__assign">' + assignRow + '</tr>' +
      '<tr class="map-grid__head">' + headRow + '</tr></thead>' +
      '<tbody>' + bodyHtml + '</tbody></table></div>' + note;
  }

  // 二維表右側必填鐵軌：每個當前模式必填欄位一列；未指派灰「待指派」，已指派綠「✓ + 來源欄名」。
  function requiredRailHtml(eligibility, mappingState) {
    var items = eligibility.required.map(function (f) {
      var val = mappingState.draft[f.key];
      var done = !!val;
      var tail = done
        ? '<span class="map-rail__col">' + Ui.esc(val) + '</span>'
        : '<span class="map-rail__pending">待指派</span>';
      return '<li class="map-rail__item' + (done ? ' is-done' : '') + '">' +
        '<span class="map-rail__mark" aria-hidden="true">' + (done ? '✓' : '') + '</span>' +
        '<span class="map-rail__label">' + Ui.esc(f.label) + '</span>' +
        tail + '</li>';
    }).join('');

    var title = eligibility.canCommit
      ? '必填欄位已全部指派'
      : '尚缺 ' + eligibility.missing.length + ' 個必填欄位';
    return '<aside class="map-rail' + (eligibility.canCommit ? ' is-complete' : '') + '">' +
      '<h4 class="map-rail__title">' + title + '</h4>' +
      '<ul class="map-rail__list">' + items + '</ul></aside>';
  }

  // 標準化政策未補齊時的就近提示（與必填鐵軌同一位置語彙）；後端仍是權威驗證。
  function optionProblemsHtml(eligibility) {
    if (!eligibility.problems || !eligibility.problems.length) { return ''; }
    return '<p class="form-notice" data-bind="mapping-option-problems">尚需補齊：' +
      eligibility.problems.map(Ui.esc).join('、') + '</p>';
  }

  function gridEditSection(kind, title, fields, modes, importInfo, mappingState, mode, committed, matches) {
    var banner = '';
    if (mappingState.invalidatedByImport) {
      banner = '<p class="mapping-section__warn">來源資料已變更，原配對已失效；請確認下方對應後重新提交。</p>';
    } else if (committed && !matches) {
      banner = '<p class="mapping-section__warn">下方修改尚未生效，目前仍以已提交版本執行；' +
        '按「重新確認配對」套用，或「還原為已提交版本」放棄修改。</p>';
    }

    var modeRadios = modes.map(function (m) {
      return '<label class="mode-option">' +
        '<input type="radio" name="mode-' + kind + '" value="' + m.value + '"' +
          (mode === m.value ? ' checked' : '') + '>' +
        '<span>' + m.label + '</span></label>';
    }).join('');

    var litField = literalFieldFor(fields, mode);
    var literalHtml = '';
    if (litField) {
      var litVal = mappingState.draft[litField.key] || '';
      var litReq = Ui.isRequired(litField, mode);
      literalHtml = '<div class="map-literal">' +
        '<label class="map-literal__label">' + Ui.esc(litField.label) +
          (litReq ? ' <em class="form__req">*</em>' : '') + '</label>' +
        '<input class="form__input map-literal__input" type="text" data-mapping-key="' + litField.key +
          '" value="' + Ui.esc(litVal) + '" placeholder="如 D 或 1">' +
        '<span class="map-literal__hint">借方的代碼字面值（不是欄位名稱）</span></div>';
    }

    var eligibility = mappingCommitEligibility(fields, mode, mappingState, kind);

    var actions =
      '<button type="button" class="btn btn--ghost" data-action="suggest-' + kind + '">自動建議</button>' +
      '<button type="button" class="btn" data-action="commit-' + kind + '"' +
        (eligibility.canCommit ? '' : ' disabled') + '>' +
        (committed ? '重新確認配對' : '確認配對') + '</button>' +
      (committed
        ? '<button type="button" class="btn btn--ghost" data-action="restore-' + kind + '">還原為已提交版本</button>'
        : '');

    return '<section class="mapping-section" data-bind="mapping-' + kind + '">' +
        '<h3 class="mapping-section__title">' + title + '</h3>' +
        uiModeToggleHtml() +
        banner +
        '<div class="mode-group">' + modeRadios + '</div>' +
        '<div class="map-layout">' +
          '<div class="map-layout__main">' +
            twoDimMappingTable(kind, fields, importInfo, mappingState, mode) +
            literalHtml +
          '</div>' +
          requiredRailHtml(eligibility, mappingState) +
        '</div>' +
        (kind === 'gl' ? glOptionsHtml(importInfo, mappingState) : '') +
        optionProblemsHtml(eligibility) +
        '<div class="panel__actions">' + actions + '</div>' +
      '</section>';
  }

  function classicEditSection(kind, title, fields, modes, importInfo, mappingState, mode, committed, matches) {
    var banner = '';
    if (mappingState.invalidatedByImport) {
      banner = '<p class="mapping-section__warn">來源資料已變更，原配對已失效；請確認下方對應後重新提交。</p>';
    } else if (committed && !matches) {
      banner = '<p class="mapping-section__warn">下方修改尚未生效，目前仍以已提交版本執行；' +
        '按「重新確認配對」套用，或「還原為已提交版本」放棄修改。</p>';
    }

    var modeRadios = modes.map(function (m) {
      return '<label class="mode-option">' +
        '<input type="radio" name="mode-' + kind + '" value="' + m.value + '"' +
          (mode === m.value ? ' checked' : '') + '>' +
        '<span>' + m.label + '</span></label>';
    }).join('');

    var eligibility = mappingCommitEligibility(fields, mode, mappingState, kind);
    var excluded = excludedFieldKeys(kind);

    var rows = fields.filter(function (field) {
      return excluded.indexOf(field.key) < 0;
    }).map(function (field) {
      var required = Ui.isRequired(field, mode);
      var current = mappingState.draft[field.key] || '';

      var control;
      if (field.literal) {
        control = '<input class="form__input mapping-table__input" type="text" data-mapping-key="' + field.key +
          '" value="' + Ui.esc(current) + '" placeholder="如 D 或 1">';
      } else {
        var options = '<option value="">—</option>' + importInfo.columns.map(function (col) {
          return '<option value="' + Ui.esc(col) + '"' + (col === current ? ' selected' : '') + '>' +
            Ui.esc(col) + '</option>';
        }).join('');
        control = '<select class="mapping-table__select" data-mapping-key="' + field.key + '">' +
          options + '</select>';
      }

      return '<tr class="mapping-table__row' + (required ? ' is-required' : '') + '">' +
          '<td class="mapping-table__label">' + field.label +
            (required ? ' <em class="form__req">*</em>' : '') + '</td>' +
          '<td class="mapping-table__key">' + field.key + '</td>' +
          '<td>' + control + '</td>' +
        '</tr>';
    }).join('');

    var actions =
      '<button type="button" class="btn btn--ghost" data-action="suggest-' + kind + '">自動建議</button>' +
      '<button type="button" class="btn btn--ghost" data-action="preview-source-' + kind +
        '" title="開啟資料預覽，對照欄位名稱與實際內容">預覽來源資料</button>' +
      '<button type="button" class="btn" data-action="commit-' + kind + '"' +
        (eligibility.canCommit ? '' : ' disabled') + '>' +
        (committed ? '重新確認配對' : '確認配對') + '</button>' +
      (committed
        ? '<button type="button" class="btn btn--ghost" data-action="restore-' + kind + '">還原為已提交版本</button>'
        : '');

    return '<section class="mapping-section" data-bind="mapping-' + kind + '">' +
        '<h3 class="mapping-section__title">' + title + '</h3>' +
        uiModeToggleHtml() +
        banner +
        '<div class="mode-group">' + modeRadios + '</div>' +
        '<div class="map-layout">' +
          '<div class="map-layout__main mapping-table-wrap"><table class="mapping-table">' +
            '<thead><tr><th>JET 邏輯欄位</th><th>欄位代碼</th><th>來源欄位</th></tr></thead>' +
            '<tbody>' + rows + '</tbody>' +
          '</table></div>' +
          requiredRailHtml(eligibility, mappingState) +
        '</div>' +
        (kind === 'gl' ? glOptionsHtml(importInfo, mappingState) : '') +
        optionProblemsHtml(eligibility) +
        '<div class="panel__actions">' + actions + '</div>' +
      '</section>';
  }

  // mapping.commit.gl 的 additive payload：只有配對過帳狀態時才帶 policy（manifest：
  // 未配對時不得提供）；rdeFields 只送使用者勾選的欄位，新欄省略 fieldId。
  function glCommitPayload(mapping, mode) {
    var options = glOptions();
    var payload = {
      mapping: mapping,
      amountMode: mode,
      approvalDateMode: options.approvalDateMode,
      manualAutoPolicy: {
        manualValues: options.manualAutoPolicy.manualValues.slice(),
        automaticValues: options.manualAutoPolicy.automaticValues.slice()
      },
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

  // 接受值集合的加入／移除：一律先 trim，再以不分大小寫比對去重（與系統端同一口徑）。
  function toggleCodeValue(values, value, include) {
    var trimmed = String(value == null ? '' : value).trim();
    if (!trimmed) { return values.slice(); }
    var next = values.filter(function (item) {
      return normalizedCode(item) !== normalizedCode(trimmed);
    });
    if (include) { next.push(trimmed); }
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
    section.querySelectorAll('[data-option-bind="approvalDateMode"]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        if (!radio.checked) { return; }
        // 衍生模式與「傳票核准日」來源欄互斥：切換到 sameAsPostDate 時同步移除既有指派，
        // 讓畫面與送出的 payload 不會出現互相矛盾的組合。
        if (radio.value === 'sameAsPostDate') {
          Store.setMappingDraft('gl', 'docDate', '');
        }
        Store.patchGlMappingOptions({ approvalDateMode: radio.value });
      });
    });

    section.querySelectorAll('[data-action="load-posting-profile"]').forEach(function (button) {
      button.addEventListener('click', function () {
        ensureValueProfile('gl', Store.getState().mapping.gl.draft.postingStatus);
      });
    });

    section.querySelectorAll('[data-action="load-manual-profile"]').forEach(function (button) {
      button.addEventListener('click', function () {
        ensureValueProfile('gl', Store.getState().mapping.gl.draft.manual);
      });
    });

    section.querySelectorAll('[data-posting-value]').forEach(function (checkbox) {
      checkbox.addEventListener('change', function () {
        var policy = glOptions().postingStatusPolicy || { acceptedValues: [], includeBlank: false };
        setPostingStatusPolicy({
          acceptedValues: toggleCodeValue(
            policy.acceptedValues || [], checkbox.getAttribute('data-posting-value'), checkbox.checked)
        });
      });
    });

    section.querySelectorAll('[data-remove-posting-value]').forEach(function (button) {
      button.addEventListener('click', function () {
        var policy = glOptions().postingStatusPolicy || { acceptedValues: [], includeBlank: false };
        setPostingStatusPolicy({
          acceptedValues: toggleCodeValue(
            policy.acceptedValues || [], button.getAttribute('data-remove-posting-value'), false)
        });
      });
    });

    var addPostingValue = section.querySelector('[data-action="add-posting-value"]');
    if (addPostingValue) {
      addPostingValue.addEventListener('click', function () {
        var input = section.querySelector('[data-bind="posting-value-add"]');
        if (!input || !input.value.trim()) { return; }
        var policy = glOptions().postingStatusPolicy || { acceptedValues: [], includeBlank: false };
        setPostingStatusPolicy({
          acceptedValues: toggleCodeValue(policy.acceptedValues || [], input.value, true)
        });
      });
    }

    var includeBlank = section.querySelector('[data-option-bind="includeBlank"]');
    if (includeBlank) {
      includeBlank.addEventListener('change', function () {
        setPostingStatusPolicy({ includeBlank: includeBlank.checked });
      });
    }

    section.querySelectorAll('[data-manual-assign]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        if (!radio.checked) { return; }
        var value = radio.getAttribute('data-manual-assign');
        var policy = glOptions().manualAutoPolicy;
        Store.patchGlMappingOptions({
          manualAutoPolicy: {
            manualValues: toggleCodeValue(policy.manualValues, value, radio.value === 'manual'),
            automaticValues: toggleCodeValue(policy.automaticValues, value, radio.value === 'automatic')
          }
        });
      });
    });

    section.querySelectorAll('[data-remove-code]').forEach(function (button) {
      button.addEventListener('click', function () {
        var value = button.getAttribute('data-remove-code');
        var group = button.getAttribute('data-code-group');
        var policy = glOptions().manualAutoPolicy;
        Store.patchGlMappingOptions({
          manualAutoPolicy: {
            manualValues: group === 'manual'
              ? toggleCodeValue(policy.manualValues, value, false) : policy.manualValues.slice(),
            automaticValues: group === 'automatic'
              ? toggleCodeValue(policy.automaticValues, value, false) : policy.automaticValues.slice()
          }
        });
      });
    });

    section.querySelectorAll('[data-action="add-code"]').forEach(function (button) {
      button.addEventListener('click', function () {
        var group = button.getAttribute('data-code-group');
        var input = section.querySelector('[data-bind="' + group + '-code-add"]');
        if (!input || !input.value.trim()) { return; }
        var policy = glOptions().manualAutoPolicy;
        Store.patchGlMappingOptions({
          manualAutoPolicy: {
            manualValues: group === 'manual'
              ? toggleCodeValue(policy.manualValues, input.value, true) : policy.manualValues.slice(),
            automaticValues: group === 'automatic'
              ? toggleCodeValue(policy.automaticValues, input.value, true) : policy.automaticValues.slice()
          }
        });
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

    section.querySelectorAll('[data-rde-label]').forEach(function (input) {
      input.addEventListener('input', function () {
        var column = input.getAttribute('data-rde-label');
        Store.patchGlMappingOptionsQuiet({
          rdeFields: (glOptions().rdeFields || []).map(function (field) {
            return field.sourceColumn === column
              ? Object.assign({}, field, { label: input.value })
              : field;
          })
        });
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

  function bindMappingSection(container, kind, fields) {
    var section = container.querySelector('[data-bind="mapping-' + kind + '"]');
    if (!section) { return; }

    if (kind === 'gl' && section.querySelector('[data-bind="gl-options"]')) {
      bindGlOptions(section);
    }

    // 摘要卡動作：重新配對（展開編輯）、預覽標準化資料。
    var remapBtn = section.querySelector('[data-action="remap-' + kind + '"]');
    if (remapBtn) {
      remapBtn.addEventListener('click', function () {
        editing[kind] = true;
        Store.touch();
      });
    }

    var previewTargetBtn = section.querySelector('[data-action="preview-target-' + kind + '"]');
    if (previewTargetBtn && Ui.openDataPreview) {
      previewTargetBtn.addEventListener('click', function () {
        Ui.openDataPreview(kind === 'gl' ? 'glEntries' : 'tbBalances');
      });
    }

    // 標頭下拉：指派來源欄 → JET 欄位（維持一對一；字面值欄不視為欄位指派）。
    var literalKeys = fields.filter(function (f) { return f.literal; }).map(function (f) { return f.key; });
    section.querySelectorAll('[data-map-col]').forEach(function (sel) {
      sel.addEventListener('change', function () {
        // 指派前先記下橫向捲動位置：assignColumnToField 會 bump 觸發全步重繪、新表捲動歸零，
        // 重繪後 bindMappingSection 依 pendingGridScroll 還原（見下方），使用者不必反覆左右捲動。
        var wrap = sel.closest('.map-grid-wrap');
        pendingGridScroll[kind] = wrap ? wrap.scrollLeft : null;
        Store.assignColumnToField(kind, sel.getAttribute('data-map-col'), sel.value, literalKeys);
      });
    });

    // 模式切換：清不適用欄位指派後設模式。
    section.querySelectorAll('input[name="mode-' + kind + '"]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        changeMode(kind, fields, radio.value);
      });
    });

    // 字面值輸入（grid/classic 共用）與 classic 的欄位下拉：寫入 draft。
    // INPUT（字面值）或已提交時重建——讓 grid 必填鐵軌與偏離橫幅即時更新；classic 的 SELECT 不重建以保流暢。
    section.querySelectorAll('[data-mapping-key]').forEach(function (control) {
      control.addEventListener('change', function () {
        Store.setMappingDraft(kind, control.getAttribute('data-mapping-key'), control.value.trim());
        if (control.tagName === 'INPUT' || Store.getState().mapping[kind].committed) { Store.touch(); }
      });
    });

    // 還原為已提交版本：草稿與模式回到快照，收回摘要卡。
    var restoreBtn = section.querySelector('[data-action="restore-' + kind + '"]');
    if (restoreBtn) {
      restoreBtn.addEventListener('click', function () {
        var committed = Store.getState().mapping[kind].committed;
        if (!committed || !committed.mapping) { return; }
        editing[kind] = false;
        Store.replaceMappingDraft(kind, Object.assign({}, committed.mapping));
        Store.setMappingMode(kind, committed.mode);
      });
    }

    // 草稿態（或已提交摘要卡）有二維表 → 惰性載入來源原貌。
    var gridWrap = section.querySelector('.map-grid-wrap');
    if (gridWrap) {
      ensureSourcePreview(kind, Store.getState().importState[kind]);
      // 還原上一輪指派前捕捉到的橫向捲動；只在確由欄位指派引發的重繪才還原，還原後清空。
      if (pendingGridScroll[kind] != null) {
        gridWrap.scrollLeft = pendingGridScroll[kind];
        pendingGridScroll[kind] = null;
      }
    }

    // 簡易清單的「預覽來源資料」：開資料預覽看來源原貌（grid 模式無此鈕）。
    var previewSourceBtn = section.querySelector('[data-action="preview-source-' + kind + '"]');
    if (previewSourceBtn && Ui.openDataPreview) {
      previewSourceBtn.addEventListener('click', function () {
        Ui.openDataPreview(kind === 'gl' ? 'glStaging' : 'tbStaging');
      });
    }

    var suggestBtn = section.querySelector('[data-action="suggest-' + kind + '"]');
    if (suggestBtn) {
      suggestBtn.addEventListener('click', function () {
        var importInfo = Store.getState().importState[kind];
        if (!importInfo) { return; }

        var fieldDefs = fields
          .filter(function (f) { return !f.literal; })
          .map(function (f) { return { key: f.key, label: f.label }; });

        Ui.run('自動建議', function () {
          return global.JetApi.mappingAutoSuggest({
            fields: fieldDefs,
            columns: importInfo.columns
          }).then(function (data) {
            var merged = Object.assign({}, Store.getState().mapping[kind].draft, data.suggested || {});
            Store.replaceMappingDraft(kind, merged);
            Store.addMessage('已套用自動建議（' +
              Object.keys(data.suggested || {}).length + ' 個欄位）。', 'info');
          });
        });
      });
    }

    var commitBtn = section.querySelector('[data-action="commit-' + kind + '"]');

    if (commitBtn) {
      commitBtn.addEventListener('click', function () {
        var current = Store.getState().mapping[kind];
        var mapping = {};
        Object.keys(current.draft).forEach(function (key) {
          if (current.draft[key]) { mapping[key] = current.draft[key]; }
        });

        var label = kind === 'gl' ? 'GL 配對' : 'TB 配對';
        var mode = kind === 'gl' ? current.amountMode : current.changeMode;

        Ui.run('提交' + label, function () {
          var startedAt = Date.now();
          var promise = kind === 'gl'
            ? global.JetApi.mappingCommitGl(glCommitPayload(mapping, mode))
            : global.JetApi.mappingCommitTb({ mapping: mapping, changeMode: mode });

          return promise.then(function (data) {
            editing[kind] = false;
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
              options: kind === 'gl' ? canonicalGlOptions(data) : null,
              // 成功提交一律由目前 writer 寫入 format v2；舊版旗標據此重新推導。
              formatVersion: 2
            });
            Store.addMessage(
              label + '已提交，已標準化 ' + Number(data.projectedRowCount).toLocaleString() +
              ' 列，耗時 ' + ((Date.now() - startedAt) / 1000).toFixed(1) + ' 秒。', 'info');
            // 後端非阻斷提醒（如必填欄整欄空白、疑似配錯欄）：逐則以警示色呈現，使用者立即看得到。
            (data.warnings || []).forEach(function (w) {
              Store.addMessage(label + '提醒：' + w, 'warn');
            });
          });
        });
      });
    }
  }

  Ui.registerStep('mapping', render);
})(window);
