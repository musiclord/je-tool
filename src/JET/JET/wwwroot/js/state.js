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
    // system.databaseInfo 回報的 sqlServer.configured：true、false，或尚未查到時為 null。
    // 只用來在建立案件時停用 SQL Server 選項；不持久化、不進 resetWorkflow、不進任何後端 payload。
    sqlServerConfigured: null,
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
      // 匯入與 project.load 都含有效人數、原始列數、空白與重複數及 GL 比對人數。
      // 舊名單的原始統計可為 null；matchedPreparerCount 為 null 表示 GL 尚未確認建立人員配對。
      authorizedPreparer: null,
      calendar: null           // { holidayCount, makeupDayCount, calendarImported, nonWorkingDays, nonWorkingDaysConfigured }
    },
    // committed = 已提交快照 { projectedRowCount, committedUtc, mapping, mode }（resume 時 projectedRowCount 可為 null）；
    // invalidatedByImport = 來源變更使配對失效（顯示說明橫幅，重新提交後解除）。
    // droppedFields = 來源變更後，草稿裡被拿掉的欄位（新資料沒有那個來源欄），提示審計員重新選。
    // options = GL 專有的投影政策草稿（核准日三態、過帳狀態政策、人工／自動代碼、攸關資料元素欄位）。
    mapping: {
      gl: {
        draft: {}, amountMode: 'dual', options: freshGlOptions(),
        committed: null, invalidatedByImport: false, reimportDraftOrigin: null, droppedFields: []
      },
      tb: { draft: {}, changeMode: 'debitCredit', committed: null, invalidatedByImport: false, reimportDraftOrigin: null, droppedFields: [] }
    },
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
    // 開案時用目前規則檢查已儲存情境的結果（project.load.filterScenarioCheck）；
    // 只用來說明哪些情境無法套用、下一步怎麼做，不參與步驟判定。
    filterScenarioCheck: null,
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
    dataGeneration: 0,
    // 後端回報篩選命中失效的次數（V7）。第五步據此清掉已載入的命中；修改案件資料不再換掉 project 物件，
    // 所以不能再靠「換了案件」順便清掉。
    filterResultGeneration: 0
  };

  // GL 投影政策草稿的初值：與後端 mapping.commit.gl 的預設一致（核准日未配、無過帳狀態政策、
  // 人工 1／自動 0、未選任何攸關資料元素欄位）。
  function freshManualAutoPolicy() {
    return { manualValues: ['1'], automaticValues: ['0'] };
  }

  function freshGlOptions() {
    return {
      approvalDateMode: 'unmapped',
      postingStatusPolicy: null,
      manualAutoPolicy: freshManualAutoPolicy(),
      rdeFields: []
    };
  }

  // 人工／自動代碼是某一個來源欄的解讀方式，不得在取消配對或換欄後沿用到另一個欄位。
  function resetManualAutoPolicyIfSourceChanged(previousSource) {
    var currentSource = state.mapping.gl.draft.manual || null;
    if ((previousSource || null) !== currentSource) {
      state.mapping.gl.options.manualAutoPolicy = freshManualAutoPolicy();
    }
  }

  function resetPostingStatusPolicyIfSourceChanged(previousSource) {
    if ((previousSource || null) !== (state.mapping.gl.draft.postingStatus || null)) {
      state.mapping.gl.options.postingStatusPolicy = null;
    }
  }

  // 核心配對已佔用的 GL 來源欄。這是「哪些欄還能當攸關資料元素」的唯一規則來源，
  // 畫面的候選清單與全選都只呼叫它。借方與貸方代碼是字面值，不是欄名，不算佔用。
  function usedGlSourceColumns() {
    var used = Object.create(null);
    var draft = state.mapping.gl.draft;
    Object.keys(draft).forEach(function (key) {
      var value = draft[key];
      if (key !== 'dcDebitCode' && key !== 'dcCreditCode' && value) { used[value] = true; }
    });
    return used;
  }

  // 核心欄位與攸關資料元素不能共用同一來源欄。配對改變時立即清掉已被核心欄位使用的 RDE，
  // 避免畫面把它藏起來後仍送出一份後端必定拒絕的草稿。
  function removeCoreMappedRdeFields() {
    var used = usedGlSourceColumns();
    state.mapping.gl.options.rdeFields = (state.mapping.gl.options.rdeFields || []).filter(function (field) {
      return !used[field.sourceColumn];
    });
  }

  function syncApprovalSource() {
    var gl = state.mapping.gl;
    if (gl.draft.docDate) {
      gl.options.approvalDateMode = 'mapped';
    } else if (gl.options.approvalDateMode === 'mapped') {
      gl.options.approvalDateMode = 'unmapped';
    }
  }

  // 來源資料換過之後，草稿只留新資料仍有的來源欄；拿掉的記在 droppedFields，配對畫面據此請審計員重新選。
  // 借方與貸方代碼都是字面值，不受來源欄改變影響。人工/自動與過帳狀態的來源欄被拿掉時，
  // 它們的代碼設定跟著清掉，和審計員自己取消那個欄位的結果一樣（D16）。
  function reconcileDraftWithColumns(kind, columns) {
    var available = Object.create(null);
    (columns || []).forEach(function (column) { available[column] = true; });
    var mapping = state.mapping[kind];
    var dropped = [];
    var previousManualSource = kind === 'gl' ? mapping.draft.manual : null;
    var previousPostingSource = kind === 'gl' ? mapping.draft.postingStatus : null;
    Object.keys(mapping.draft).forEach(function (key) {
      var column = mapping.draft[key];
      if (key === 'dcDebitCode' || key === 'dcCreditCode' || !column || available[column]) { return; }
      dropped.push({ key: key, column: column });
      delete mapping.draft[key];
    });
    if (kind === 'gl') {
      syncApprovalSource();
      resetManualAutoPolicyIfSourceChanged(previousManualSource);
      resetPostingStatusPolicyIfSourceChanged(previousPostingSource);
      mapping.options.rdeFields = (mapping.options.rdeFields || []).filter(function (field) {
        if (available[field.sourceColumn]) { return true; }
        dropped.push({ rde: true, column: field.sourceColumn, label: field.label });
        return false;
      });
    }
    mapping.droppedFields = dropped;
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
  // 還原，步驟模組不得各自發明保留機制。已文件化的例外：filter-step 規則值編輯
  // （patchFilterRule＋softRefreshReadback／softExpirePreviewPane，連續輸入且重建成本高）；
  // 欄位配對的借方代碼與攸關資料元素顯示名稱（setMappingLiteralQuiet、patchGlMappingOptionsQuiet）。
  // 後者每次 input 就地更新必填清單、提示與「確認配對」可用性，失焦時不重建：滑鼠按下確認鈕會先讓
  // 輸入框失焦，若此時重建，按鈕會在按下與放開之間被換掉，第一次點擊就不會送出（2026-10-02 W3）。
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

  // 後端明示本次修改影響哪些畫面快取；此處不保存另一份審計依賴表或推算報告過期狀態。
  function invalidateDerivedResults(options) {
    var clearValidation = !!(options && options.validation);
    var clearPrescreen = !!(options && options.prescreen);
    var clearFilter = !!(options && options.filter);
    if (clearValidation) { state.lastRuns.validate = null; }
    if (clearPrescreen) { state.lastRuns.prescreen = null; }
    // 只有命中失效時清掉預覽；已存情境是否一起清除，由後端的 filterScenarios 決定。
    // 新 WorkingPaper 還記錄 filterDataRevision，因此部分情境重算不必偽造全案已更新。
    if (clearFilter) {
      state.filter.preview = null;
      filterDraftRev++;
      state.filterResultGeneration++;
    }
    // 使用者 2026-10-07 裁定：上游修改會清除後面步驟的篩選情境，第五步回到預設狀態。
    if (options && options.filterScenarios) {
      var hadScenarios = state.filter.savedScenarios.length > 0 || state.filter.draft.groups.length > 0;
      state.filter.savedScenarios = [];
      state.filter.draft = { name: '', rationale: '', groups: [] };
      state.filter.preview = null;
      state.filter.previewExpired = false;
      state.filterResultRef = null;
      state.filterScenarioCheck = null;
      filterDraftRev++;
      if (hadScenarios) { Store.addMessage('前面的資料已更改，請重新設定篩選情境。', 'info'); }
    }
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

    // 背景設定查詢只通知畫面就地更新資料庫選項與提示，不能重建尚未儲存的建案表單。
    // 是否可選仍由明確的 false 判斷，不把未知狀態當作已確認沒有設定。
    setSqlServerConfigured: function (configured) {
      var next = configured === true ? true : (configured === false ? false : null);
      if (next === state.sqlServerConfigured) { return; }
      state.sqlServerConfigured = next;
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
      // 案件名稱是唯一必要識別；案件編號已是選填，不能再拿空白案件編號當頂部主識別。
      state.caseId = project ? project.projectId : null;
      state.caseClient = project ? project.entityName : null;
      bump();
    },

    // 修改案件資料只更新後端回傳的摘要與失效狀態，不套用開案流程，也不丟棄未儲存的篩選草稿。
    updateProjectMetadata: function (result) {
      // V7：同一個案件只更新原本 project 物件的欄位。各步驟以物件身分判斷「是否換了案件」，
      // 換掉物件會把值摘要、已載入的明細與展開的結果都當成舊案件清掉。該清的結果由下方依後端失效清單處理。
      var current = state.project;
      if (current && result.project && current.projectId === result.project.projectId) {
        Object.keys(current).forEach(function (key) {
          if (!Object.prototype.hasOwnProperty.call(result.project, key)) { delete current[key]; }
        });
        Object.assign(current, result.project);
        result = Object.assign({}, result, { project: current });
      }
      state.project = result.project;
      state.caseId = result.project.projectId;
      state.caseClient = result.project.entityName;
      // 舊 project.update 回應只帶 artifacts；新契約的 null 是尚未讀到清單，不可用 alias 冒充成功。
      var response = Object.prototype.hasOwnProperty.call(result, 'reportArtifacts') ? result
        : Object.assign({}, result, { reportArtifacts: result.artifacts });
      Store.applyMutationEffects(response);
    },

    applyMutationEffects: function (result) {
      result = result || {};
      var invalidated = result.invalidatedResults || {};
      invalidateDerivedResults(invalidated);
      if (result.staleState) {
        state.staleState = { validation: !!result.staleState.validation,
          prescreen: !!result.staleState.prescreen, filter: !!result.staleState.filter };
      }
      if (Array.isArray(result.reportArtifacts)) { state.reportArtifacts = result.reportArtifacts.slice(); }
      if (result.reportArtifactWarning) { Store.addMessage(result.reportArtifactWarning, 'warn'); }
      if (invalidated.validation || invalidated.prescreen || invalidated.filter) { bumpData(); } else { bump(); }
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
          committed: null, invalidatedByImport: false, reimportDraftOrigin: null, droppedFields: []
        },
        tb: { draft: {}, changeMode: 'debitCredit', committed: null, invalidatedByImport: false, reimportDraftOrigin: null, droppedFields: [] }
      };
      state.taxonomy = null;
      state.staleState = { validation: false, prescreen: false, filter: false };
      state.lastRuns = { validate: null, prescreen: null };
      state.filterResultRef = null;
      state.filterScenarioCheck = null;
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
      // 原本已提交時標記失效原因（配對步驟顯示「來源資料已變更」橫幅）。草稿保留，
      // 只拿掉新資料沒有的來源欄。
      state.mapping[kind].invalidatedByImport = !!state.mapping[kind].committed || state.mapping[kind].invalidatedByImport;
      state.mapping[kind].reimportDraftOrigin = state.importState[kind] ? 'current' : null;
      state.importState[kind] = info;
      state.mapping[kind].committed = null;
      if (kind === 'gl' && state.importState.authorizedPreparer) {
        state.importState.authorizedPreparer = Object.assign({}, state.importState.authorizedPreparer,
          { matchedPreparerCount: null });
      }
      if (info && Array.isArray(info.columns)) {
        reconcileDraftWithColumns(kind, info.columns);
      } else {
        state.mapping[kind].droppedFields = [];
      }
      bumpData(); // 匯入落地：staging 預覽（原貌）內容已變
    },

    // 模組區域狀態（如匯入精靈的待匯入清單）變動時觸發面板重建。
    touch: function () {
      bump();
    },

    setCalendarState: function (info) {
      state.importState.calendar = info;
      bumpData(); // 假日／補班／非工作日：dateDimension 預覽內容已變
    },

    // 科目配對（無欄位配對步驟，匯入即投影；不影響 GL/TB 配對狀態）。
    setAccountMappingState: function (info) {
      state.importState.accountMapping = info;
      bumpData(); // 科目配對匯入：accountMappings 預覽內容已變
    },

    // 授權編製人員清單（整份替換的設定檔，匯入即生效；不影響 GL/TB 配對狀態）。
    setAuthorizedPreparerState: function (info) {
      state.importState.authorizedPreparer = info;
      bumpData(); // 授權編製人員清單匯入：authorizedPreparers 預覽內容已變
    },

    // GL 配對回傳的最新比對摘要；名單內容沒有改變，不再清除相依計算結果。
    refreshAuthorizedPreparerState: function (info) {
      state.importState.authorizedPreparer = info;
      bump();
    },

    setLastRun: function (kind, summary) {
      state.lastRuns[kind] = summary || null;
      if (summary) { state.staleState[kind === 'validate' ? 'validation' : kind] = false; }
      bump();
    },

    setFilterResultRef: function (resultRef) {
      state.filterResultRef = resultRef || null;
      state.staleState.filter = false;
      bump();
    },

    setFilterScenarioCheck: function (check) {
      state.filterScenarioCheck = check || null;
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
        var published = data.artifacts || (data.artifact ? [data.artifact] : []);
        // Only the just-published artifact proves successful calculation; a catalog may contain old files.
        published.forEach(function (artifact) {
          if (artifact.stale) { return; }
          if (artifact.kind === 'criteriaSelectionReport' ||
              (artifact.kind === 'workingPaper' && data.filterResultsCurrent !== false)) { state.staleState.filter = false; }
        if (artifact.kind === 'prescreenReport') { state.staleState.prescreen = false; }
      });
        var catalogComplete = Array.isArray(data.reportArtifacts) && published.every(function (artifact) {
          return data.reportArtifacts.some(function (item) { return item.artifactId === artifact.artifactId; });
        });
        if (catalogComplete) {
          Store.setReportArtifacts(data.reportArtifacts);
        } else {
          var warning = data.reportArtifactWarning || (Array.isArray(data.reportArtifacts)
            ? '檔案已產生，報告清單暫時無法更新。可開啟案件資料夾查看，無須重新產生。' : '');
          Store.upsertReportArtifacts(published.map(function (artifact, index) {
            return index === 0 && warning
              ? Object.assign({}, artifact, { catalogWarning: warning }) : artifact;
          }));
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
      // 同一份草稿改了條件才提示「條件已變更」；換成新草稿（新增情境、取消編輯、開啟已存情境、套用範例）
      // 時重新開始，不沿用上一份草稿的預覽狀態。
      var sameDraft = !!draft && draft === state.filter.draft;
      state.filter.draft = draft || { name: '', rationale: '', groups: [] };
      state.filter.previewExpired = sameDraft && (!!state.filter.preview || !!state.filter.previewExpired);
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

    // 成功保存或移除後，一次更新清單、結果與編輯狀態，避免重繪看到不同版本。
    applyFilterCommit: function (data, draftPatch, clearDraft) {
      state.filter.savedScenarios = data.scenarios || [];
      state.filterResultRef = state.filter.savedScenarios.length ? data.resultRef || null : null;
      // 儲存成功表示整批已通過目前規則的檢查，開案時列出的問題不再適用。
      state.filterScenarioCheck = null;
      state.staleState.filter = false;
      if (clearDraft) {
        state.filter.draft = { name: '', rationale: '', groups: [] };
        state.filter.preview = null;
        state.filter.previewExpired = false;
        filterDraftRev++;
      } else if (draftPatch) { Object.assign(state.filter.draft, draftPatch); }
      bump();
    },

    // 可作為攸關資料元素的 GL 來源欄：匯入欄位扣掉核心配對已佔用者。順序沿用匯入欄序。
    availableGlRdeColumns: function () {
      var importInfo = state.importState.gl;
      var used = usedGlSourceColumns();
      return ((importInfo && importInfo.columns) || []).filter(function (column) { return !used[column]; });
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
      bumpData(); // 科目分類改變：科目配對預覽的顯示分類已變
    },

    // project.load.staleState 的直接鏡像（不由前端猜測）。
    setStaleState: function (staleState) {
      state.staleState = {
        validation: !!(staleState && staleState.validation),
        prescreen: !!(staleState && staleState.prescreen),
        filter: !!(staleState && staleState.filter)
      };
      bump();
    },

    // 字面值欄（借方代碼）連續輸入專用：只寫草稿並 notify，不重建面板。呼叫端必須在同一次輸入
    // 就地更新衍生畫面（mapping-step 的 refreshMappingDerived）。字面值不是來源欄名，不牽動核准日、
    // 人工／自動代碼、過帳狀態政策或攸關資料元素欄位，所以不需要 setMappingDraft 的連動處理。
    setMappingLiteralQuiet: function (kind, key, value) {
      if (value) {
        state.mapping[kind].draft[key] = value;
      } else {
        delete state.mapping[kind].draft[key];
      }
      notify();
    },

    setMappingDraft: function (kind, key, column) {
      var previousManualSource = kind === 'gl' ? state.mapping.gl.draft.manual : null;
      var previousPostingSource = kind === 'gl' ? state.mapping.gl.draft.postingStatus : null;
      if (column) {
        state.mapping[kind].draft[key] = column;
      } else {
        delete state.mapping[kind].draft[key];
      }
      if (kind === 'gl') {
        if (key === 'docDate') { syncApprovalSource(); }
        resetManualAutoPolicyIfSourceChanged(previousManualSource);
        resetPostingStatusPolicyIfSourceChanged(previousPostingSource);
        removeCoreMappedRdeFields();
      }
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
      var previousManualSource = kind === 'gl' ? draft.manual : null;
      var previousPostingSource = kind === 'gl' ? draft.postingStatus : null;
      Object.keys(draft).forEach(function (k) {
        if (skip.indexOf(k) < 0 && draft[k] === column) { delete draft[k]; }
      });
      if (fieldKey) { draft[fieldKey] = column; }
      if (kind === 'gl') {
        if (previousApprovalColumn !== draft.docDate) { syncApprovalSource(); }
        resetManualAutoPolicyIfSourceChanged(previousManualSource);
        resetPostingStatusPolicyIfSourceChanged(previousPostingSource);
        removeCoreMappedRdeFields();
      }
      bump(); // 指派會牽動其他標頭的選取狀態，需重建面板
    },

    replaceMappingDraft: function (kind, draft) {
      var previousManualSource = kind === 'gl' ? state.mapping.gl.draft.manual : null;
      var previousPostingSource = kind === 'gl' ? state.mapping.gl.draft.postingStatus : null;
      state.mapping[kind].draft = Object.assign({}, draft || {});
      if (kind === 'gl') {
        syncApprovalSource();
        resetManualAutoPolicyIfSourceChanged(previousManualSource);
        resetPostingStatusPolicyIfSourceChanged(previousPostingSource);
        removeCoreMappedRdeFields();
      }
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

    // 從報告還原欄位配對草稿：後端已把兩份 metadata 與目前 import columns 整批驗證完畢，
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

    // 重開案件時，重新匯入後還沒重新確認的配對：把 project.load 帶回的上次確認配對當草稿，
    // 只留新資料仍有的來源欄，並和同一次開啟中重新匯入時一樣標成「來源資料已變更」。它不是已生效的配對。
    // previous = { mapping, mode, options }；options 只有 GL 有。
    restorePreviousMapping: function (kind, previous, columns) {
      var mapping = state.mapping[kind];
      mapping.draft = Object.assign({}, previous.mapping || {});
      if (kind === 'gl') {
        mapping.amountMode = previous.mode || 'dual';
        mapping.options = previous.options
          ? JSON.parse(JSON.stringify(previous.options)) : freshGlOptions();
        removeCoreMappedRdeFields();
      } else {
        mapping.changeMode = previous.mode || 'debitCredit';
      }
      reconcileDraftWithColumns(kind, columns);
      mapping.invalidatedByImport = true;
      mapping.reimportDraftOrigin = 'previous';
      bump();
    },

    // 失效旗標只由 setImportResult 與 restorePreviousMapping 立起；任何明確的提交狀態設定（含 resume 的 null）都解除。
    setMappingCommitted: function (kind, result) {
      // 載入案件時，草稿與提交結果可能來自同一份 JSON；保存獨立快照，編輯不能改掉還原依據。
      state.mapping[kind].committed = result ? JSON.parse(JSON.stringify(result)) : null;
      state.mapping[kind].invalidatedByImport = false;
      state.mapping[kind].reimportDraftOrigin = null;
      state.mapping[kind].droppedFields = [];
      bumpData(); // 配對提交（或載入還原）：target 標準化預覽（glEntries／tbBalances）內容已變
    },

    addMessage: function (text, level) {
      if (level === undefined) { level = 'info'; }
      if ((level !== 'info' && level !== 'warn') || typeof text !== 'string' || !text.trim()) { return; }
      state.messages.unshift({
        id: ++messageSeq,
        text: text,
        level: level,
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
