/*
  Frontend UI state boundary.
  Keep transient view state here; do not store GL/TB row populations.
*/
(function (global) {
  'use strict';

  // 流程階段定義（僅作前端導航與呈現用途，非後端契約）。
  // 6 步模型：validation 先證明母體可用；選用的 prescreen 與它共用「資料驗證與測試」步驟，
  // 但不是進入進階條件篩選的前置。
  var STEPS = [
    { id: 'create', label: '建立案件' },
    { id: 'import', label: '匯入資料' },
    { id: 'mapping', label: '欄位配對' },
    { id: 'validate', label: '資料驗證與測試' },
    { id: 'filter', label: '進階條件篩選' },
    { id: 'export', label: '匯出底稿' }
  ];

  // 僅保存輕量的 UI 狀態，不得保存完整 GL/TB row set。
  var state = {
    view: 'picker',            // 'picker' | 'workflow'
    currentStepIndex: 0,
    bridgeReady: false,
    devToolsEnabled: false,    // system.ping 回報；Release 組建為 false（開發面板隱藏）
    // system.whoAmI 回報的當前使用者身分 { principal, shortName, userNumber, numberSource }（純 UI 顯示：
    // 右上角身分徽章與 picker 身分註記）。只 notify、不 bump、不持久化、不進 resetWorkflow、不進任何後端 payload。
    currentUser: null,
    caseId: null,
    caseClient: null,
    messages: [],
    messagesPanelOpen: false,  // 「狀態與訊息」預設收合為右側窄欄
    messagesUnseen: 0,         // 收合期間新增的訊息數（展開時歸零）
    stepFlowCollapsed: false,  // 文件流當前步驟是否手動收合（純 UI；不持久化、不進後端 payload）
    dataPreviewCollapsed: false, // 右欄常駐資料預覽是否收合（純 UI；比照 stepFlowCollapsed，不持久化、不進後端 payload）
    overviewOpen: false,       // 流程總覽 modal 是否開啟（純 UI；notify 不 bump、不持久化、不進 resetWorkflow、不進任何後端 payload）
    busy: false,
    busyLabel: null,           // 進行中作業的顯示標籤（取自 Ui.run 的 label；純 UI，不 bump、不持久化、不進後端 payload）
    busyDetail: null,          // 長作業進度事件的輕量顯示文字；不承載權威結果
    activeRequestId: null,     // 目前 Ui.run 的 bridge requestId，只供 operation.cancel
    cancellationRequested: false,
    projects: [],              // picker 最近接受的 snapshot：本機清單，或使用者手動同步後的本機＋線上清單
    pickerFeedback: null,      // picker 就地錯誤（訊息面板在 workflow app-body 內，picker 期間不可見）
    pickerFeedbackContext: null, // { projectId, errorCode, correlationId, exportedPath, devExportedPath }；只供複製、支援日誌與 DEV 診斷日誌匯出
    // project.list 的頂層 online 區塊 { reachable, principal, message? }（僅手動線上同步後存在）。
    // null 表示目前是 project.listLocal snapshot、尚未手動同步；picker 不得把它誤稱為「線上無案件」。
    online: null,
    project: null,             // 當前專案 metadata（project.load / create 回傳）
    importState: {
      gl: null,                // { batchId, rowCount, columns, fileName, sources: [...] }
      tb: null,
      accountMapping: null,    // { batchId, rowCount, fileName, importedUtc, hasAnyCategory, hasRevenue, hasCounterpart }
      authorizedPreparer: null, // 匯入當下 { batchId, rowCount, fileName, importedUtc }；resume(project.load) 只 { rowCount }
      calendar: null           // { holidayCount, makeupDayCount, calendarImported, nonWorkingDays, nonWorkingDaysConfigured }
    },
    // committed = 已提交快照 { projectedRowCount, committedUtc, mapping, mode }（resume 時 projectedRowCount 可為 null）；
    // invalidatedByImport = 來源變更使配對失效（顯示說明橫幅，重新提交後解除）。
    // options = GL 專有的投影政策草稿（核准日三態、過帳狀態政策、人工／自動代碼、攸關資料元素欄位）；
    // formatVersion = 後端 project.load 回報的該側 committed mapping 來源版本（1 | 2 | null）。
    mapping: {
      gl: {
        draft: {}, amountMode: 'dual', options: freshGlOptions(),
        committed: null, formatVersion: null, invalidatedByImport: false
      },
      tb: { draft: {}, changeMode: 'debitCredit', committed: null, formatVersion: null, invalidatedByImport: false }
    },
    // 舊版配對需重新確認（project.load.mappingReviewRequired 的鏡像；成功 recommit 後依兩側
    // formatVersion 重新推導，讓修復路徑不必重開案件才解除）。
    mappingReviewRequired: false,
    // 專案科目分類（project.load.taxonomy）：{ revision, categories }；尚未載入時為 null。
    taxonomy: null,
    // 後端判定的結果失效狀態（project.load.staleState）：三個獨立布林，
    // 不得以 latestRuns === null 或 artifact stale 代算。
    staleState: { validation: false, prescreen: false, filter: false },
    // 欄位配對介面偏好（session 級；不被 resetWorkflow 清除）。'classic'（預設三欄表）| 'grid'（二維表）
    mappingUiMode: 'classic',
    // 最近一次 validate.run / prescreen.run 的 response（resume 自 project.load.latestRuns）。
    lastRuns: { validate: null, prescreen: null },
    // 篩選結果版本與專案內報告索引（resume 自 project.load）。
    // artifact 只保留 wire metadata，不存絕對路徑或報告內容。
    filterResultRef: null,
    reportArtifacts: [],
    validationOutput: { reports: null, template: null },
    // 進階條件篩選：populationScope 是尚未提交的母體選擇；已提交母體只讀
    // filterResultRef.populationScope，兩者不得混成同一個狀態。AST 草稿恆為物件。
    filter: {
      populationScope: 'auditPeriod',
      draft: { name: '', rationale: '', groups: [] },
      preview: null,
      savedScenarios: []
    },
    // 資料變動版本號：app.js 以此判斷是否需要重建中央面板。
    contentVersion: 0,
    // 底層資料世代訊號：只在「後端已寫入、且改變某個可預覽資料集」的成功回應後遞增
    // （匯入落地、配對提交、科目配對／授權清單／行事曆匯入）。與 contentVersion 不同——
    // contentVersion 對每次 UI 資料變動（切模式、指派欄、編草稿…）都會跳，過於嘈雜，
    // 不適合當「資料是否真的變了」的判準；dataGeneration 只對真正的資料落地事件跳，
    // 供資料預覽面板據此精準作廢快取並重抓（避免每個 action 都盲目重抓大母體）。
    dataGeneration: 0
  };

  // GL 投影政策草稿的初值：與後端 mapping.commit.gl 的預設一致（核准日未配、無過帳狀態政策、
  // 人工 1／自動 0、未選任何攸關資料元素欄位）。
  function freshGlOptions() {
    return {
      approvalDateMode: 'unmapped',
      postingStatusPolicy: null,
      manualAutoPolicy: { manualValues: ['1'], automaticValues: ['0'] },
      rdeFields: []
    };
  }

  function syncApprovalSource() {
    var gl = state.mapping.gl;
    if (gl.draft.docDate) {
      gl.options.approvalDateMode = 'mapped';
    } else if (gl.options.approvalDateMode === 'mapped') {
      gl.options.approvalDateMode = 'unmapped';
    }
  }

  var listeners = [];

  // 訊息流水號：供前端做增量渲染，辨識哪些訊息是新加入的。
  var messageSeq = 0;

  // 篩選草稿版本號：草稿內容每變動一次就遞增。預覽請求送出前擷取當時版本，
  // 回應抵達時比對版本；母體固定為 auditPeriod（filter-step 的預覽回呼守衛）。
  // 單調遞增、跨 resetWorkflow 不歸零：歸零會讓「舊專案的在途請求＋新專案」出現版本撞號的窗口。
  var filterDraftRev = 0;

  // 通知慣例（整個前端唯一）：凡是會改變任何衍生畫面輸出的寫入，一律 bump()（contentVersion
  // 進版、面板重建）；「只 notify()」僅保留給「唯一視覺反映就是使用者正在編輯的那個控制項本身」
  // 的連續文字輸入（input 事件），且必須在 blur 邊界配一次延遲 bump 收斂，讓按鈕可用性、清單與
  // 提示等衍生畫面在互動結束時對齊。select／radio／checkbox 是離散提交，永遠直接 bump。
  // 重建後的輸入焦點與捲動由 app.js renderContent 依焦點識別屬性與 data-preserve-scroll 統一
  // 還原，步驟模組不得各自發明保留機制。已文件化的唯一例外：filter-step 規則值編輯
  // （patchFilterRule＋softRefreshReadback／softExpirePreviewPane，連續輸入且重建成本高）。
  function notify() {
    for (var i = 0; i < listeners.length; i++) {
      listeners[i](state);
    }
  }

  function bump() {
    state.contentVersion++;
    notify();
  }

  // 資料落地專用 bump：同時推進 dataGeneration（供資料預覽作廢快取）與 contentVersion（面板重建），
  // 一次 notify。只由「後端寫入成功且改變可預覽資料集」的 setter 呼叫，見 dataGeneration 說明。
  function bumpData() {
    state.dataGeneration++;
    bump();
  }

  // GL/TB、配對、科目分類、授權清單或行事曆落地後，後端會在同一交易清除規則命中。
  // 前端依同一份依賴範圍丟棄衍生結果與快取；保留已存情境定義，讓使用者可在新母體上重新產檔。
  function invalidateDerivedResults(options) {
    var clearValidation = !!(options && options.validation);
    var clearPrescreen = !!(options && options.prescreen);
    var clearFilter = !!(options && options.filter);
    if (clearValidation) { state.lastRuns.validate = null; }
    if (clearPrescreen) { state.lastRuns.prescreen = null; }
    // 情境 revision 只描述已存定義，跨資料重投影仍保留；正式 Criteria/WorkingPaper
    // 另以 validationRunId 綁定資料世代，並以 scenarioRevision 綁定條件版本。
    if (clearFilter) {
      state.filter.preview = null;
      filterDraftRev++;
    }
    state.reportArtifacts = state.reportArtifacts.map(function (artifact) {
      var source = artifact.sourceRef || {};
      var stale = (clearValidation && !!source.validationRunId) ||
        (clearPrescreen && artifact.kind === 'prescreenReport' && !!source.prescreenRunId) ||
        (clearFilter && (artifact.kind === 'criteriaSelectionReport' || artifact.kind === 'workingPaper') &&
          !!source.scenarioRevision);
      return artifact.stale || !stale ? artifact : Object.assign({}, artifact, { stale: true });
    });
  }

  // 舊版配對旗標的重新推導：後端規則是「任何已提交的 GL 或 TB mapping 仍是 format v1 即為 true」。
  // 這裡只依已鏡射的 formatVersion 重算，讓成功 recommit 後不必重開案件才解除封鎖；
  // 沒有 committed mapping 的一側不算舊版，與後端一致。
  function refreshMappingReviewRequired() {
    state.mappingReviewRequired = ['gl', 'tb'].some(function (kind) {
      return !!state.mapping[kind].committed && state.mapping[kind].formatVersion === 1;
    });
  }

  var Store = {
    STEPS: STEPS,

    getState: function () {
      return state;
    },

    subscribe: function (fn) {
      if (typeof fn === 'function') {
        listeners.push(fn);
      }
    },

    setStepIndex: function (index) {
      if (index < 0 || index >= STEPS.length) {
        return;
      }
      state.currentStepIndex = index;
      notify();
    },

    setBridgeReady: function (ready) {
      state.bridgeReady = !!ready;
      notify();
    },

    setDevToolsEnabled: function (enabled) {
      state.devToolsEnabled = !!enabled;
      notify();
    },

    // system.whoAmI 結果進 state（純 UI 顯示身分徽章與 picker 註記）。
    // 比照 stepFlowCollapsed 等純 UI 狀態：只 notify，不 bump、不持久化、不進 resetWorkflow、不進任何後端 payload。
    setCurrentUser: function (user) {
      state.currentUser = user || null;
      notify();
    },

    setView: function (view) {
      state.view = view;
      bump();
    },

    setBusy: function (busy, label, requestId) {
      state.busy = !!busy;
      state.busyLabel = busy ? (label || null) : null;
      state.busyDetail = null;
      state.activeRequestId = busy ? (requestId || null) : null;
      state.cancellationRequested = false;
      notify();
    },

    setBusyRequest: function (requestId) {
      if (!state.busy) { return; }
      state.activeRequestId = requestId || null;
      notify();
    },

    setBusyDetail: function (detail) {
      if (!state.busy) { return; }
      state.busyDetail = detail || null;
      notify();
    },

    setCancellationRequested: function (requested) {
      if (!state.busy) { return; }
      state.cancellationRequested = !!requested;
      notify();
    },

    setProjects: function (projects, online) {
      state.projects = projects || [];
      // online 缺席（後端未部署雙來源欄位）時存為 null＝reachable 未知；一次 bump 兩者同步更新。
      state.online = online || null;
      bump();
    },

    setPickerFeedback: function (message, context) {
      state.pickerFeedback = message || null;
      state.pickerFeedbackContext = message && context ? {
        projectId: context.projectId || null,
        errorCode: context.errorCode || null,
        correlationId: context.correlationId || null,
        exportedPath: context.exportedPath || null,
        devExportedPath: context.devExportedPath || null
      } : null;
      // 純 picker 呈現狀態；renderPicker 的 memo key 直接納入此值，不需 bump workflow contentVersion。
      notify();
    },

    setProject: function (project) {
      state.project = project;
      state.caseId = project ? project.projectCode : null;
      state.caseClient = project ? project.entityName : null;
      bump();
    },

    // 離開專案（回 picker / 建立新案件）時清空 workflow 狀態，
    // 避免上一個案件的資料殘留在建立案件等步驟。
    resetWorkflow: function () {
      state.project = null;
      state.caseId = null;
      state.caseClient = null;
      state.importState = { gl: null, tb: null, accountMapping: null, authorizedPreparer: null, calendar: null };
      state.mapping = {
        gl: {
          draft: {}, amountMode: 'dual', options: freshGlOptions(),
          committed: null, formatVersion: null, invalidatedByImport: false
        },
        tb: { draft: {}, changeMode: 'debitCredit', committed: null, formatVersion: null, invalidatedByImport: false }
      };
      state.mappingReviewRequired = false;
      state.taxonomy = null;
      state.staleState = { validation: false, prescreen: false, filter: false };
      state.lastRuns = { validate: null, prescreen: null };
      state.filterResultRef = null;
      state.reportArtifacts = [];
      state.validationOutput = { reports: null, template: null };
      state.filter = {
        populationScope: 'auditPeriod',
        draft: { name: '', rationale: '', groups: [] },
        preview: null,
        savedScenarios: []
      };
      filterDraftRev++; // 草稿被整份重置，在途的預覽回應一併作廢
      state.currentStepIndex = 0;
      bump();
    },

    setImportResult: function (kind, info) {
      // 匯入（replace 或 append）會使後端 committed mapping 失效，前端狀態同步歸零；
      // 原本已提交時標記失效原因（配對步驟顯示「來源資料已變更」橫幅）。
      state.mapping[kind].invalidatedByImport = !!state.mapping[kind].committed;
      state.importState[kind] = info;
      state.mapping[kind].committed = null;
      // 沒有 committed mapping 的一側不算舊版（與後端同一條規則），因此重匯後
      // 立即丟掉該側的 format 版本並重新推導舊版旗標。
      state.mapping[kind].formatVersion = null;
      refreshMappingReviewRequired();
      invalidateDerivedResults(kind === 'gl'
        ? { validation: true, prescreen: true, filter: true }
        : { validation: true });
      bumpData(); // 匯入落地：staging 預覽（原貌）內容已變
    },

    // 模組區域狀態（如匯入精靈的待匯入清單）變動時觸發面板重建。
    touch: function () {
      bump();
    },

    setCalendarState: function (info) {
      state.importState.calendar = info;
      invalidateDerivedResults({ prescreen: true, filter: true });
      bumpData(); // 假日／補班／非工作日：dateDimension 預覽內容已變
    },

    // 科目配對（無欄位配對步驟，匯入即投影；不影響 GL/TB 配對狀態）。
    setAccountMappingState: function (info) {
      state.importState.accountMapping = info;
      invalidateDerivedResults({ prescreen: true, filter: true });
      bumpData(); // 科目配對匯入：accountMappings 預覽內容已變
    },

    // 授權編製人員清單（整份替換的設定檔，匯入即生效；不影響 GL/TB 配對狀態）。
    setAuthorizedPreparerState: function (info) {
      state.importState.authorizedPreparer = info;
      invalidateDerivedResults({ prescreen: true, filter: true });
      bumpData(); // 授權編製人員清單匯入：authorizedPreparers 預覽內容已變
    },

    setLastRun: function (kind, summary) {
      state.lastRuns[kind] = summary || null;
      bump();
    },

    setFilterResultRef: function (resultRef) {
      state.filterResultRef = resultRef || null;
      bump();
    },

    // 相容既有 caller 的單一入口；查核母體固定為 auditPeriod。
    setFilterPopulationScope: function () {
      if (state.filter.populationScope === 'auditPeriod') { return; }
      state.filter.populationScope = 'auditPeriod';
      state.filter.preview = null;
      filterDraftRev++;
      bump();
    },

    // project.load 專用：舊 scope 不回灌成可執行狀態；唯一母體仍是查核期間。
    restoreFilterPopulationScope: function () {
      state.filter.populationScope = 'auditPeriod';
      state.filter.preview = null;
      filterDraftRev++;
      bump();
    },

    setReportArtifacts: function (artifacts) {
      state.reportArtifacts = (artifacts || []).slice();
      bump();
    },

    setValidationOutput: function (kind, result) {
      state.validationOutput[kind] = result;
      bump();
    },

    applyReportExport: function (data) {
      if (Array.isArray(data.reportArtifacts)) {
        Store.setReportArtifacts(data.reportArtifacts);
      } else {
        Store.upsertReportArtifacts(data.artifacts || (data.artifact ? [data.artifact] : []));
      }
    },

    // 一般報告同名覆蓋；Working Paper 每次新增版本，只更新同一 artifactId。
    upsertReportArtifacts: function (artifacts) {
      var incoming = (artifacts || []).filter(Boolean);
      if (incoming.length === 0) { return; }

      var incomingKinds = {};
      var incomingIds = {};
      incoming.forEach(function (artifact) {
        if (artifact.kind !== 'workingPaper') { incomingKinds[artifact.kind] = true; }
        incomingIds[artifact.artifactId] = true;
      });

      var next = state.reportArtifacts
        .filter(function (artifact) {
          return !incomingKinds[artifact.kind] && !incomingIds[artifact.artifactId];
        })
        .concat(incoming);

      state.reportArtifacts = next;
      bump();
    },

    setFilterDraft: function (draft) {
      state.filter.draft = draft || { name: '', rationale: '', groups: [] };
      state.filter.previewExpired = !!state.filter.preview || !!state.filter.previewExpired;
      state.filter.preview = null; // 草稿結構變動使預覽失效
      filterDraftRev++;
      bump();
    },

    // 規則值編輯（input/select）只 notify 不重建面板，避免輸入焦點被吃掉。
    // 值編輯同樣改變命中集合：舊預覽即刻失效（清空），版本遞增讓在途的預覽回應作廢。
    patchFilterRule: function (groupIndex, ruleIndex, patch) {
      var draft = state.filter.draft;
      if (!draft.groups[groupIndex] || !draft.groups[groupIndex].rules[ruleIndex]) {
        return;
      }
      Object.assign(draft.groups[groupIndex].rules[ruleIndex], patch);
      state.filter.previewExpired = !!state.filter.preview || !!state.filter.previewExpired;
      state.filter.preview = null;
      filterDraftRev++;
      notify();
    },

    // 名稱／動機等 meta 編輯不影響命中集合：不清預覽、不動版本號。
    patchFilterDraftMeta: function (patch) {
      Object.assign(state.filter.draft, patch);
      notify();
    },

    getFilterDraftRev: function () {
      return filterDraftRev;
    },

    setFilterPreview: function (preview) {
      state.filter.preview = preview;
      state.filter.previewExpired = false;
      bump();
    },

    setSavedScenarios: function (list) {
      state.filter.savedScenarios = list || [];
      bump();
    },

    // GL 投影政策草稿（核准日三態、過帳狀態政策、人工／自動代碼、攸關資料元素欄位）。
    // 只 patch 使用者編輯中的欄位；送出形狀與合法性由 mapping-step 組裝、後端裁定。
    patchGlMappingOptions: function (patch) {
      Object.assign(state.mapping.gl.options, patch);
      if (Object.prototype.hasOwnProperty.call(patch, 'approvalDateMode')
          && patch.approvalDateMode !== 'mapped') {
        delete state.mapping.gl.draft.docDate;
      }
      bump();
    },

    // 只改值不改結構時使用（輸入框連續輸入）：不重建面板以保住輸入焦點。
    patchGlMappingOptionsQuiet: function (patch) {
      Object.assign(state.mapping.gl.options, patch);
      notify();
    },

    replaceGlMappingOptions: function (options) {
      state.mapping.gl.options = options || freshGlOptions();
      bump();
    },

    // project.load.taxonomy 的鏡像；科目分類 mutation 只使 prescreen 與 filter 命中失效。
    setTaxonomy: function (snapshot) {
      state.taxonomy = snapshot || null;
      bump();
    },

    setTaxonomyAfterSave: function (snapshot) {
      state.taxonomy = snapshot || null;
      invalidateDerivedResults({ prescreen: true, filter: true });
      state.staleState = Object.assign({}, state.staleState, { prescreen: true, filter: true });
      bumpData(); // 科目分類改變：科目配對預覽的顯示分類已變
    },

    // project.load.mappingReviewRequired / staleState 的直接鏡像（不由前端猜測）。
    setMappingReviewRequired: function (required) {
      state.mappingReviewRequired = !!required;
      bump();
    },

    setStaleState: function (staleState) {
      state.staleState = {
        validation: !!(staleState && staleState.validation),
        prescreen: !!(staleState && staleState.prescreen),
        filter: !!(staleState && staleState.filter)
      };
      bump();
    },

    setMappingDraft: function (kind, key, column) {
      if (column) {
        state.mapping[kind].draft[key] = column;
      } else {
        delete state.mapping[kind].draft[key];
      }
      if (kind === 'gl' && key === 'docDate') { syncApprovalSource(); }
      // 草稿餵給必填鐵軌、「確認配對」可用性與 GL 政策區的分支顯示，依通知慣例必須 bump；
      // 重繪後的焦點與捲動由 renderContent 統一還原，不在此犧牲衍生畫面的即時性。
      bump();
    },

    // 對照表格用：把某來源欄指派給某 JET 欄位（空 fieldKey = 此欄不對應）。
    // 維持一對一：先解除任何已指向此欄的欄位，再指派；設 draft[fieldKey] 會自動覆蓋該欄位舊指向。
    assignColumnToField: function (kind, column, fieldKey, literalKeys) {
      var skip = literalKeys || [];
      var draft = state.mapping[kind].draft;
      var previousApprovalColumn = draft.docDate;
      Object.keys(draft).forEach(function (k) {
        if (skip.indexOf(k) < 0 && draft[k] === column) { delete draft[k]; }
      });
      if (fieldKey) { draft[fieldKey] = column; }
      if (kind === 'gl' && previousApprovalColumn !== draft.docDate) { syncApprovalSource(); }
      bump(); // 指派會牽動其他標頭的選取狀態，需重建面板
    },

    replaceMappingDraft: function (kind, draft) {
      state.mapping[kind].draft = Object.assign({}, draft || {});
      if (kind === 'gl') { syncApprovalSource(); }
      bump();
    },

    restoreCommittedMapping: function (kind) {
      var mapping = state.mapping[kind];
      var committed = mapping.committed;
      if (!committed || !committed.mapping) { return; }
      mapping.draft = Object.assign({}, committed.mapping);
      if (kind === 'gl') {
        mapping.amountMode = committed.mode;
        mapping.options = committed.options
          ? JSON.parse(JSON.stringify(committed.options)) : freshGlOptions();
        if (!committed.options) { syncApprovalSource(); }
      } else {
        mapping.changeMode = committed.mode;
      }
      bump();
    },

    // P2 report round-trip：後端已把兩份 metadata 與目前 import columns 整批驗證完畢，
    // 前端只做一次原子鏡像。既有 committed/invalidated 狀態刻意保留，草稿不冒充已生效配對。
    restoreMappingDrafts: function (bundle) {
      state.mapping.gl.draft = Object.assign({}, bundle.gl.mapping || {});
      state.mapping.gl.amountMode = bundle.gl.amountMode;
      // v2 草稿另帶核准日三態、過帳狀態政策、人工／自動代碼與攸關資料元素定義。
      // reader 已把 v1 正規化成 v2 形狀，因此這裡只做一次原子鏡像，不再逐欄推導。
      state.mapping.gl.options = {
        approvalDateMode: bundle.gl.approvalDateMode || 'unmapped',
        postingStatusPolicy: bundle.gl.postingStatusPolicy || null,
        manualAutoPolicy: bundle.gl.manualAutoPolicy || {
          manualValues: ['1'], automaticValues: ['0']
        },
        rdeFields: (bundle.gl.rdeFields || []).slice()
      };
      state.mapping.tb.draft = Object.assign({}, bundle.tb.mapping || {});
      state.mapping.tb.changeMode = bundle.tb.changeMode;
      bump();
    },

    setMappingMode: function (kind, mode) {
      if (kind === 'gl') {
        state.mapping.gl.amountMode = mode;
      } else {
        state.mapping.tb.changeMode = mode;
      }
      bump();
    },

    setMappingUiMode: function (uiMode) {
      state.mappingUiMode = uiMode === 'grid' ? 'grid' : 'classic';
      bump();
    },

    // 失效旗標只由 setImportResult 立起；任何明確的提交狀態設定（含 resume 的 null）都解除。
    // formatVersion 隨 committed 一起設定：resume 取後端回報值，成功 commit 一律是目前 writer 版本 2。
    setMappingCommitted: function (kind, result) {
      // 載入案件時，草稿與提交結果可能來自同一份 JSON；保存獨立快照，編輯不能改掉還原依據。
      state.mapping[kind].committed = result ? JSON.parse(JSON.stringify(result)) : null;
      state.mapping[kind].formatVersion = result ? (result.formatVersion || null) : null;
      state.mapping[kind].invalidatedByImport = false;
      refreshMappingReviewRequired();
      invalidateDerivedResults(kind === 'gl'
        ? { validation: true, prescreen: true, filter: true }
        : { validation: true });
      bumpData(); // 配對提交（或載入還原）：target 標準化預覽（glEntries／tbBalances）內容已變
    },

    addMessage: function (text, level) {
      state.messages.unshift({
        id: ++messageSeq,
        text: text,
        level: level || 'info',
        time: new Date().toLocaleTimeString('zh-Hant', { hour12: false })
      });
      // 僅保留最近數則訊息，避免無上限累積（完整歷史持久化於專案資料庫 app_message_log）。
      state.messages = state.messages.slice(0, 30);

      if (!state.messagesPanelOpen) {
        state.messagesUnseen++;
      }

      notify();
    },

    toggleMessagesPanel: function () {
      state.messagesPanelOpen = !state.messagesPanelOpen;
      if (state.messagesPanelOpen) {
        state.messagesUnseen = 0;
      }
      notify();
    },

    // 文件流當前步驟的展開／收合（純 UI；不重建資料，只 notify 讓 app.js 換排版）。
    setStepFlowCollapsed: function (collapsed) {
      state.stepFlowCollapsed = !!collapsed;
      notify();
    },

    // 右欄常駐資料預覽的展開／收合（純 UI；只 notify 讓 app.js 切 .data-preview--collapsed）。
    setDataPreviewCollapsed: function (collapsed) {
      state.dataPreviewCollapsed = !!collapsed;
      notify();
    },

    // 流程總覽 modal 的開／關（純 UI；只 notify 讓 app.js 切 .overview-modal 顯示並 render 內容）。
    setOverviewOpen: function (open) {
      state.overviewOpen = !!open;
      notify();
    },

    // 以持久化歷史（log.recent，新→舊）取代目前訊息清單：project.load 後還原面板。
    // fromLog 標記讓持久化訂閱者不回寫（避免重複落庫）。
    // id 由舊到新遞增指派（增量渲染依賴「清單首位 id 最大」的不變量）。
    seedMessages: function (entries) {
      var source = (entries || []).slice(0, 30);
      var mapped = new Array(source.length);

      for (var i = source.length - 1; i >= 0; i--) {
        mapped[i] = {
          id: ++messageSeq,
          text: source[i].text,
          level: source[i].level || 'info',
          time: new Date(source[i].occurredUtc).toLocaleTimeString('zh-Hant', { hour12: false }),
          fromLog: true
        };
      }

      state.messages = mapped;
      state.messagesUnseen = 0;
      notify();
    }
  };

  global.JetStore = Store;
})(window);
