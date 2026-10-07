/*
  前端殼層與啟動入口（bootstrap boundary）。
  持有：主渲染迴圈、專案選擇畫面、步驟導航、訊息區、啟動流程。
  共用核心在 js/ui-core.js（JetUi）；各步驟渲染器在 js/steps/*.js 自行註冊；
  本檔不承載任何權威業務規則。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  // 單庫容量預警門檻（MB；純前端呈現、可後調）。system.databaseInfo 回報的整庫大小（databaseSizeMb，
  // 含所有專案 schema 與交易記錄檔）超過此值時，於訊息面板補一則 warn 提示 DBA 留意容量／規劃備份與清理。
  // 8192 MB ≈ 已淘汰的 SQL Server Express 10 GB 上限的 80%，作為單庫「偏大」的軟參考點；零商業邏輯。
  var DB_CAPACITY_WARN_MB = 8192;

  // 取消鈕只追蹤會實際形成長作業的 request；背景 heartbeat／log／query 即使在 busy
  // 期間送出，也不能覆寫目前長作業的 requestId。
  var CANCELLABLE_ACTIONS = {
    'project.list': true,
    'project.load': true,
    'import.inspectFile': true,
    'import.previewFile': true,
    'import.gl.fromFile': true,
    'import.tb.fromFile': true,
    'import.accountMapping.fromFile': true,
    'import.authorizedPreparer.fromFile': true,
    'import.authorizedPreparer.clear': true,
    'import.holiday': true,
    'import.makeupDay': true,
    'import.holiday.fromFile': true,
    'import.makeupDay.fromFile': true,
    'mapping.commit.gl': true,
    'mapping.commit.tb': true,
    'validate.run': true,
    'prescreen.run': true,
    'filter.preview': true,
    'filter.commit': true,
    'export.validationArtifacts': true,
    'export.accountMappingTemplate': true,
    'export.calendarTemplates': true,
    'export.prescreenReport': true,
    'export.criteriaSelectionReport': true,
    'export.workpaperStream': true
  };

  // 增量渲染狀態
  var lastNavKey = null;
  var lastContentKey = null;
  var contentComposition = null;
  var lastStepScrollKey = null; // 上次已捲頂的 (view, step)；步驟／視圖切換時把中欄捲回頂部
  var lastPickerKey = null;
  var lastMessageId = 0;
  var messagesEmptyShown = false;
  // 開機 bridge 探測是否已有結論（systemPing 成敗任一終態、或無 host 時即 true）。
  // 讓身分徽章區分「偵測中」（尚未結論）與「主機連線失敗」（已結論且未就緒）；純渲染旗標、不進 state、不進後端 payload。
  var bridgeProbeSettled = false;

  // 本階段共用的最小焦點 seam：app 殼層持有移動焦點與 dialog Tab 圈限，
  // 步驟模組只透過 JetFocus 使用「安全移動／延後移動」，不把此純 UI 行為擴張到 wire。
  function focusElement(element) {
    if (!element || typeof element.focus !== 'function' || !element.isConnected) { return false; }
    element.focus();
    return document.activeElement === element;
  }

  function deferFocus(resolveElement) {
    global.setTimeout(function () {
      var element = typeof resolveElement === 'function' ? resolveElement() : resolveElement;
      focusElement(element);
    }, 0);
  }

  function dialogFocusableElements(dialog) {
    if (!dialog) { return []; }
    return Array.prototype.slice.call(dialog.querySelectorAll(
      'button:not([disabled]), summary, [href], input:not([disabled]), select:not([disabled]), ' +
      'textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'))
      .filter(function (element) { return !element.hidden && element.getClientRects().length > 0; });
  }

  function trapDialogTab(dialog, event) {
    if (!event || event.key !== 'Tab') { return false; }
    var focusable = dialogFocusableElements(dialog);
    if (focusable.length === 0) { return false; }
    var first = focusable[0];
    var last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      focusElement(last);
      return true;
    }
    if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      focusElement(first);
      return true;
    }
    if (!dialog.contains(document.activeElement)) {
      event.preventDefault();
      focusElement(first);
      return true;
    }
    return false;
  }

  var Focus = {
    move: focusElement,
    defer: deferFocus,
    trapDialogTab: trapDialogTab
  };
  global.JetFocus = Focus;

  // 作業進行中的整介面阻斷：可見遮罩延遲顯示的計時器，與上一輪 busy 狀態
  //（只在「轉為 busy」時起一次計時器，避免多輪 render 疊加多個計時器）。
  var busyOverlayTimer = null;
  var busyElapsedTimer = null;
  var busyStartedAt = null;
  var busyActiveAction = null;
  var busyWasBusy = false;

  function busyMonotonicNow() {
    return global.performance && typeof global.performance.now === 'function'
      ? global.performance.now()
      : Date.now();
  }

  function formatBusyElapsed(elapsedMilliseconds) {
    var totalSeconds = Math.max(0, Math.floor(elapsedMilliseconds / 1000));
    var hours = Math.floor(totalSeconds / 3600);
    var minutes = Math.floor((totalSeconds % 3600) / 60);
    var seconds = totalSeconds % 60;
    var clock = (hours > 0 ? String(hours).padStart(2, '0') + ':' : '') +
      String(minutes).padStart(2, '0') + ':' + String(seconds).padStart(2, '0');
    return '已經過 ' + clock;
  }

  function updateBusyElapsed() {
    var elapsedEl = document.querySelector('[data-bind=busy-elapsed]');
    if (!elapsedEl || busyStartedAt === null) { return; }
    elapsedEl.textContent = formatBusyElapsed(busyMonotonicNow() - busyStartedAt);
  }

  function startBusyElapsedClock() {
    busyStartedAt = busyMonotonicNow();
    updateBusyElapsed();
    if (busyElapsedTimer === null) {
      busyElapsedTimer = global.setInterval(updateBusyElapsed, 1000);
    }
  }

  function stopBusyElapsedClock() {
    if (busyElapsedTimer !== null) {
      global.clearInterval(busyElapsedTimer);
      busyElapsedTimer = null;
    }
    busyStartedAt = null;
  }

  /* ---- 主渲染 ---------------------------------------------------------- */

  // 作業進行中的整介面阻斷 chrome（多一層防護；真正拒絕重複作業的是後端）：
  //  busy=true → 立即對 <main> 設 inert（一次擋住頂列／目錄／內容／右欄預覽／總覽 modal／dev 面板的
  //    滑鼠、鍵盤與焦點）並設 aria-busy；可見遮罩層延遲約 200ms 才顯示（<200ms 快查詢不閃遮罩）。
  //  busy=false → 清計時器、隱藏遮罩、移除 inert 與 aria-busy。
  //  每輪 render 皆冪等重設 inert/attr/label；只在「轉為 busy」時起計時器（busyWasBusy 追蹤），避免疊加。
  function applyBusyChrome(state) {
    var mainEl = document.querySelector('[data-jet-root]');
    var overlayEl = document.querySelector('[data-bind=busy-overlay]');
    var labelEl = document.querySelector('[data-bind=busy-label]');
    var elapsedEl = document.querySelector('[data-bind=busy-elapsed]');
    var guidanceEl = document.querySelector('[data-bind=busy-guidance]');
    var cancelEl = document.querySelector('[data-action="cancel-operation"]');
    if (!mainEl || !overlayEl || !labelEl) { return; }

    if (state.busy) {
      if (!busyWasBusy) { startBusyElapsedClock(); }
      mainEl.inert = true;
      mainEl.setAttribute('aria-busy', 'true');
      labelEl.textContent = state.busyLabel
        ? state.busyLabel + '中…' + (state.busyDetail || '請稍候')
        : '處理中…' + (state.busyDetail || '請稍候');
      if (elapsedEl) {
        elapsedEl.hidden = false;
        updateBusyElapsed();
      }
      if (guidanceEl) {
        var needsLongOperationGuidance = busyActiveAction === 'validate.run' ||
          busyActiveAction === 'prescreen.run';
        guidanceEl.hidden = !needsLongOperationGuidance;
        guidanceEl.textContent = needsLongOperationGuidance
          ? '大型案件可能需要較長時間，可取消'
          : '';
      }
      if (cancelEl) {
        cancelEl.hidden = !state.activeRequestId;
        cancelEl.disabled = !!state.cancellationRequested;
        cancelEl.textContent = state.cancellationRequested ? '取消中…' : '取消作業';
      }
      if (!busyWasBusy && busyOverlayTimer === null) {
        busyOverlayTimer = setTimeout(function () { overlayEl.hidden = false; }, 200);
      }
    } else {
      clearTimeout(busyOverlayTimer);
      busyOverlayTimer = null;
      stopBusyElapsedClock();
      busyActiveAction = null;
      overlayEl.hidden = true;
      mainEl.inert = false;
      mainEl.removeAttribute('aria-busy');
      if (elapsedEl) {
        elapsedEl.hidden = true;
        elapsedEl.textContent = '';
      }
      if (guidanceEl) {
        guidanceEl.hidden = true;
        guidanceEl.textContent = '';
      }
      if (cancelEl) {
        cancelEl.hidden = true;
        cancelEl.disabled = false;
        cancelEl.textContent = '取消作業';
      }
    }
    busyWasBusy = state.busy;
  }

  function render() {
    var state = Store.getState();
    if (contentComposition && (contentComposition.project !== state.project ||
        contentComposition.view !== state.view || contentComposition.step !== state.currentStepIndex)) {
      contentComposition.target = null;
    }
    var steps = Store.STEPS;
    var current = steps[state.currentStepIndex];

    document.body.classList.toggle('is-busy', state.busy);
    applyBusyChrome(state);

    var picker = Ui.$('project-picker');
    var body = Ui.$('app-body');
    var dataPreview = Ui.$('data-preview');
    var devPanel = Ui.$('dev-panel');
    var devLogPanel = Ui.$('dev-log-panel');
    var backPicker = Ui.$('back-picker');
    // 「流程總覽」鈕以 data-action 選取（無 data-bind）；比照 back-picker 的 hidden 切換模式。
    var overviewBtn = document.querySelector('[data-action="overview-open"]');
    var openProjectFolder = document.querySelector('[data-action="open-project-folder"]');

    if (state.view === 'picker') {
      // picker 不是文件流；清掉上一次 workflow 定位，讓回開同一步也會重新捲頂、對焦與宣告。
      lastStepScrollKey = null;
      var stepAnnouncer = Ui.$('step-announcer');
      if (stepAnnouncer) { stepAnnouncer.textContent = ''; }
      if (picker) { picker.hidden = false; }
      if (body) { body.hidden = true; }
      // 右欄資料預覽在 app-body 內，隨 body.hidden 一併隱藏，不需單獨切換。
      if (devPanel) { devPanel.hidden = true; }
      if (devLogPanel) { devLogPanel.hidden = true; }
      if (backPicker) { backPicker.hidden = true; }
      if (overviewBtn) { overviewBtn.hidden = true; }
      if (openProjectFolder) { openProjectFolder.hidden = true; }
      renderSummary(state, current);
      renderPicker(state);
      renderMessages(state);
      renderOverview(state);
      return;
    }

    if (picker) { picker.hidden = true; }
    if (body) { body.hidden = false; }
    // 右欄常駐；收合為純 UI 狀態，切 class 不影響資料載入節制。
    if (dataPreview) { dataPreview.classList.toggle('data-preview--collapsed', !!state.dataPreviewCollapsed); }
    // 開發面板只在 Debug 組建顯示（system.ping.devToolsEnabled；Release 連 action 都未註冊）
    if (devPanel) { devPanel.hidden = !state.devToolsEnabled; }
    if (devLogPanel) { devLogPanel.hidden = !state.devToolsEnabled; }
    if (backPicker) { backPicker.hidden = false; }
    if (overviewBtn) { overviewBtn.hidden = false; }
    if (openProjectFolder) { openProjectFolder.hidden = false; }

    renderSummary(state, current);
    renderStepNav(state, steps);
    renderContent(state, current);
    renderMessages(state);
    renderOverview(state);
  }

  function renderSummary(state, current) {
    renderIdentityBadge(state);
    Ui.setText('case-id', state.caseId || '尚未建立');
    Ui.setText('case-client', state.caseClient || '—');
    Ui.setText('case-period', state.view === 'picker' ? '—' : (auditPeriodText(state) || '—'));
    Ui.setText('case-step', state.view === 'picker' ? '選擇專案' : current.label);
    // 目前步驟＝位置（step+1 / total）；已完成步驟數走 Ui.doneStepCount，只在目錄與流程總覽呈現。
    Ui.setText('case-progress',
      state.view === 'picker' ? '—' : (state.currentStepIndex + 1) + ' / ' + Store.STEPS.length);
  }

  // 右上角身分徽章（原「主機連線」狀態徽章）。狀態序：
  // 尚未結論＝「偵測中」；bridge 就緒但無 whoAmI＝「已連線」；有身分時顯示「操作人員：{短名}」。
  // 探測已結論但未就緒＝「主機連線失敗」（沿用既有故障訊號）。title 帶完整 principal 與編號來源說明。
  function renderIdentityBadge(state) {
    var badge = Ui.$('user-identity');
    if (!badge) { return; }

    var user = state.currentUser;
    var text;
    var title;

    if (user) {
      text = '目前帳號：' + user.shortName;
      title = identityTitle(user);
    } else if (state.bridgeReady) {
      text = '已連線';
      title = '主機已連線，尚未取得使用者身分';
    } else if (bridgeProbeSettled) {
      text = '主機連線失敗';
      title = '無法與主機建立連線';
    } else {
      text = '偵測中';
      title = '正在偵測主機連線與使用者身分';
    }

    badge.textContent = text;
    badge.title = title;
  }

  // 身分徽章 title：principal 全稱＋編號來源一句（online／cached／unavailable 三態）。
  function identityTitle(user) {
    var note;
    if (user.numberSource === 'online') {
      note = '編號已向線上資料庫確認';
    } else if (user.numberSource === 'cached') {
      note = '編號來自本機快取（目前離線）';
    } else {
      note = '尚未取得使用者編號（未設定或無法連線線上資料庫）';
    }
    return '身分：' + user.principal + '，' + note;
  }

  /* ---- 專案選擇畫面 ----------------------------------------------------- */

  // Phosphor 風 inline SVG（bold/fill，非 thin-line 圖庫、非 emoji）。
  var ICON_TRASH =
    '<svg viewBox="0 0 256 256" width="16" height="16" fill="currentColor" aria-hidden="true">' +
    '<path d="M216 48h-40v-8a24 24 0 0 0-24-24h-48a24 24 0 0 0-24 24v8H40a8 8 0 0 0 0 16h8v144a16 16 0 0 0 16 16h128a16 16 0 0 0 16-16V64h8a8 8 0 0 0 0-16ZM96 40a8 8 0 0 1 8-8h48a8 8 0 0 1 8 8v8H96Zm96 168H64V64h128Zm-80-104v64a8 8 0 0 1-16 0v-64a8 8 0 0 1 16 0Zm48 0v64a8 8 0 0 1-16 0v-64a8 8 0 0 1 16 0Z"></path></svg>';
  var ICON_PLUS =
    '<svg viewBox="0 0 256 256" width="16" height="16" fill="currentColor" aria-hidden="true">' +
    '<path d="M224 128a8 8 0 0 1-8 8h-80v80a8 8 0 0 1-16 0v-80H40a8 8 0 0 1 0-16h80V40a8 8 0 0 1 16 0v80h80a8 8 0 0 1 8 8Z"></path></svg>';

  function providerText(provider) {
    if (!provider) { return '資料庫種類尚未確認'; }
    if (provider === 'sqlServer') { return 'SQL Server'; }
    if (provider === 'duckdb') { return 'DuckDB'; }
    return 'SQLite';
  }

  function providerClass(provider) {
    if (!provider) { return ''; }
    if (provider === 'sqlServer') { return 'project-provider--sqlserver'; }
    if (provider === 'duckdb') { return 'project-provider--duckdb'; }
    return 'project-provider--sqlite';
  }

  // 上次開啟時間（lastOpenedUtc 有值則顯示；否則 fallback 建立時間）。
  function pickerWhen(p) {
    var iso = p.lastOpenedUtc || p.createdUtc;
    if (!iso) { return ''; }
    var date = new Date(iso);
    if (isNaN(date.getTime())) { return ''; }
    var prefix = p.lastOpenedUtc ? '上次開啟 ' : '建立 ';
    return prefix + date.toLocaleString('zh-Hant', {
      year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false
    });
  }

  // 同步狀態徽章（小、低噪，中性灰）。syncStatus 僅 sqlServer 條目有；synced 或缺欄位＝不加徽章。
  // unreachable = online.reachable === false（失聯時 localOnly 只標「本機快取」，不斷言伺服器上找不到）。
  function syncBadgeHtml(p, unreachable) {
    var label;
    if (p.syncStatus === 'serverOnly') {
      label = '僅伺服器';
    } else if (p.syncStatus === 'localOnly') {
      label = unreachable ? '本機快取' : '本機快取（伺服器上找不到）';
    } else if (p.syncStatus === 'noAccess') {
      label = '無存取權';
    } else {
      return '';
    }
    return '<span class="sync-badge">' + Ui.esc(label) + '</span>';
  }

  // 租約鎖徽章：有 lock 的線上案件顯示「🔒 由 {持鎖者} 開啟中」；無鎖不加。
  // 純鏡像後端 project.list 的 lock 欄，不做前端擋阻——點開仍走 project.load，被鎖與否以後端權威為準。
  function lockBadgeHtml(p) {
    if (!p.lock || !p.lock.lockedBy) { return ''; }
    var by = p.lock.lockedBy;
    var shortBy = by.indexOf('\\') >= 0 ? by.slice(by.lastIndexOf('\\') + 1) : by;
    var title = '由 ' + by + '（' + (p.lock.machineName || '') + '）於 ' + (p.lock.lockedUtc || '') + ' 開啟中';
    return '<span class="lock-badge" title="' + Ui.esc(title) + '">🔒 由 ' + Ui.esc(shortBy) + ' 開啟中</span>';
  }

  // 單列專案卡片（線上／本地兩區共用；provider 小徽章保留，sqlServer 條目再依 syncStatus 加同步徽章、依 lock 加鎖徽章）。
  function projectRowHtml(p, unreachable) {
    var displayName = p.projectId;
    var entityName = p.entityName
      ? '<span class="project-row__entity">' + Ui.esc(p.entityName) + '</span>'
      : '';
    var projectCode = p.projectCode
      ? '<span class="project-row__code">' + Ui.esc(p.projectCode) + '</span>'
      : '';
    return (
      '<div class="project-row" data-project-id="' + Ui.esc(p.projectId) + '">' +
        '<button type="button" class="project-row__open" data-action="picker-open" data-project-id="' +
          Ui.esc(p.projectId) + '" data-project-provider="' + Ui.esc(p.databaseProvider) + '" ' +
          'aria-label="開啟專案 ' + Ui.esc(displayName) + '">' +
          '<span class="project-row__main">' +
            '<span class="project-row__title">' + Ui.esc(displayName) + '</span>' +
            '<span class="project-row__meta">' +
              entityName +
              projectCode +
              '<span class="project-provider ' + providerClass(p.databaseProvider) + '">' +
                Ui.esc(providerText(p.databaseProvider)) + '</span>' +
              (p.loadError ? '<span class="project-row__load-error" role="status">案件資料無法讀取：' +
                Ui.esc(p.loadError.message || '請還原備份，或另建案件重新匯入。') + '</span>' : '') +
              syncBadgeHtml(p, unreachable) +
              lockBadgeHtml(p) +
              '<span class="project-row__when">' + Ui.esc(pickerWhen(p)) + '</span>' +
            '</span>' +
          '</span>' +
        '</button>' +
        '<button type="button" class="project-row__delete" data-action="picker-delete" ' +
          'data-project-id="' + Ui.esc(p.projectId) + '" data-project-name="' + Ui.esc(displayName) + '" ' +
          'data-project-provider="' + Ui.esc(p.databaseProvider) + '" ' +
          'title="刪除專案" aria-label="刪除專案">' + ICON_TRASH + '</button>' +
      '</div>'
    );
  }

  function pickerFeedbackHtml(message, context) {
    if (!message) { return ''; }
    // DEV 按鈕只在 Debug 組建出現（system.ping.devToolsEnabled）。載入失敗時案件尚未開啟，面板內的
    // 「輸出日誌」用不到，所以原始診斷日誌的出口也放在這裡，並帶同一次 correlation。
    var devToolsEnabled = !!Store.getState().devToolsEnabled;
    var actions = context && context.projectId
      ? '<span class="picker-feedback__actions">' +
          '<button type="button" class="btn btn--ghost" data-action="picker-copy-error">複製錯誤</button>' +
          '<button type="button" class="btn btn--ghost" data-action="picker-support-export">輸出支援日誌</button>' +
          (devToolsEnabled
            ? '<button type="button" class="btn btn--ghost" data-action="picker-dev-log-export">輸出診斷日誌（DEV）</button>'
            : '') +
        '</span>'
      : '';
    var exportResult = context && context.exportedPath
      ? '<span class="picker-feedback__export-result">支援日誌已輸出：' + Ui.esc(context.exportedPath) + '</span>'
      : '';
    var devExportResult = context && context.devExportedPath
      ? '<span class="picker-feedback__export-result">診斷日誌已輸出：' + Ui.esc(context.devExportedPath) + '</span>'
      : '';
    return '<div class="picker-feedback" role="alert" aria-live="polite">' +
      '<strong class="picker-feedback__title">無法完成操作</strong>' +
      '<span class="picker-feedback__body">' + Ui.esc(message) + '</span>' +
      exportResult +
      devExportResult +
      actions +
      '</div>';
  }

  function renderPicker(state) {
    var container = Ui.$('project-picker');
    if (!container) { return; }

    // 身分註記（whoAmI）走 setCurrentUser、只 notify 不 bump contentVersion，故把它併入 memo key，
    // 讓 whoAmI 晚於首次 picker 繪製抵達時仍能重繪、把註記從 online.principal 升級為「短名，U編號」。
    var user = state.currentUser;
    var identityKey = user ? (user.shortName + '/' + user.userNumber) : '';
    var feedbackContext = state.pickerFeedbackContext || {};
    var key = 'picker|' + state.contentVersion + '|' + identityKey + '|' + (state.pickerFeedback || '') + '|' +
      (feedbackContext.projectId || '') + '|' + (feedbackContext.correlationId || '') + '|' +
      (feedbackContext.exportedPath || '') + '|' + (feedbackContext.devExportedPath || '');
    if (key === lastPickerKey) { return; }
    lastPickerKey = key;

    // online=null 表示目前只有 project.listLocal snapshot、尚未手動同步；此時不能宣稱線上無案件。
    var onlineNotSynced = state.online === null;
    var online = state.online || {};
    var reachabilityKnown = typeof online.reachable === 'boolean';
    var unreachable = online.reachable === false;

    // 分組依據：databaseProvider（sqlServer→線上、其餘→本地）。syncStatus 只影響 sqlServer 條目的徽章。
    var onlineRows = [];
    var localRows = [];
    state.projects.forEach(function (p) {
      if (p.databaseProvider === 'sqlServer') {
        onlineRows.push(projectRowHtml(p, unreachable));
      } else {
        localRows.push(projectRowHtml(p, unreachable));
      }
    });

    // 線上區標題右註記：僅在可連線且身分已知時顯示；失聯改用警示條、reachable 未知則不顯示。
    // 有 whoAmI 且含編號時用「短名，U編號」，否則沿用 project.list 的 online.principal。
    var identityLabel = (user && user.userNumber != null)
      ? Ui.esc(user.shortName) + '（U' + user.userNumber + '）'
      : (online.principal ? Ui.esc(online.principal) : '');
    var principalNote = (reachabilityKnown && !unreachable && identityLabel)
      ? '<span class="picker-section__note">以 ' + identityLabel + ' 身分檢視</span>'
      : '';

    // 失聯降級：非阻斷警示條（單一訊號紅），附後端 message（若有）；此時線上區列的是本機快取條目。
    var onlineAlert = unreachable
      ? '<div class="picker-section__alert" role="status">無法連線伺服器，僅顯示本機快取' +
          (online.message ? '：' + Ui.esc(online.message) : '') + '</div>'
      : '';

    // 未同步、同步後確實無案件、同步失聯是三個不同狀態；失聯以警示條代替空狀態句。
    var onlineBody = onlineRows.length
      ? onlineRows.join('')
      : (onlineNotSynced
          ? '<p class="picker-section__empty">尚未同步線上案件。</p>'
          : (unreachable ? '' : '<p class="picker-section__empty">目前沒有你可存取的線上案件。</p>'));
    var localBody = localRows.length
      ? localRows.join('')
      : '<p class="picker-section__empty">尚無本地案件。</p>';

    container.innerHTML =
      '<div class="picker-panel">' +
        '<h2 class="picker-panel__title">選擇專案</h2>' +
        '<p class="picker-panel__hint">選擇案件繼續作業，或建立新案件。</p>' +
        pickerFeedbackHtml(state.pickerFeedback, state.pickerFeedbackContext) +
        '<section class="picker-section">' +
          '<div class="picker-section__head">' +
            '<h3 class="picker-section__title">線上資料庫（SQL Server）</h3>' +
            principalNote +
          '</div>' +
          onlineAlert +
          '<div class="picker-section__list">' + onlineBody + '</div>' +
        '</section>' +
        '<section class="picker-section">' +
          '<div class="picker-section__head">' +
            '<h3 class="picker-section__title">本地資料庫</h3>' +
          '</div>' +
          '<div class="picker-section__list">' + localBody + '</div>' +
        '</section>' +
        '<div class="picker-panel__list">' +
          '<button type="button" class="project-row project-row--new" data-action="picker-new">' +
            '<span class="project-row__plus">' + ICON_PLUS + '</span>' +
            '<span class="project-row__main">' +
              '<span class="project-row__title">新增專案</span>' +
            '</span>' +
          '</button>' +
        '</div>' +
        '<div class="picker-panel__actions">' +
          '<button type="button" class="btn btn--ghost" data-action="picker-refresh-local">重新整理本機</button>' +
          '<button type="button" class="btn" data-action="picker-sync-online">同步線上案件</button>' +
        '</div>' +
      '</div>' +
      '<div class="modal" data-bind="delete-modal" hidden>' +
        '<div class="modal__backdrop" data-action="modal-cancel"></div>' +
        '<div class="modal__card" role="dialog" aria-modal="true" aria-labelledby="delete-modal-title">' +
          '<h3 class="modal__title" id="delete-modal-title">刪除專案</h3>' +
          '<p class="modal__body">確定要刪除「<span data-bind="delete-modal-name"></span>」嗎？' +
            '<span data-bind="delete-modal-detail"></span></p>' +
          '<button type="button" class="btn btn--ghost" data-action="delete-preview-retry" hidden>重新確認數量</button>' +
          '<div class="modal__actions">' +
            '<button type="button" class="btn btn--ghost" data-action="modal-cancel">取消</button>' +
            '<button type="button" class="btn btn--danger" data-action="modal-confirm">刪除</button>' +
          '</div>' +
        '</div>' +
      '</div>';

    bindPicker(container);
  }

  // 待確認刪除的目標（modal 開啟期間）；確認後清空。
  var pendingDelete = null;

  function bindPicker(container) {
    var modal = container.querySelector('[data-bind="delete-modal"]');
    var previewRetry = modal && modal.querySelector('[data-action="delete-preview-retry"]');

    function deleteDetail(target, countText) {
      var scope = target.provider === 'sqlServer'
        ? '將從伺服器永久刪除此案件，影響所有使用者。' : '將永久刪除此案件的資料庫。';
      return Ui.esc(countText + ' ' + scope + '案件資料夾及其中其他檔案也會一併刪除，無法復原。');
    }

    function loadDeletePreview(target) {
      var detail = modal && modal.querySelector('[data-bind="delete-modal-detail"]');
      if (!target || !detail) { return; }
      var request = {};
      target.previewRequest = request;
      detail.innerHTML = deleteDetail(target, '正在確認報告與工作底稿數量。');
      if (previewRetry) { previewRetry.hidden = true; }
      function current() { return pendingDelete === target && target.previewRequest === request && modal.isConnected !== false; }
      global.JetApi.projectDeletePreview({ projectId: target.id }).then(function (data) {
        if (!current()) { return; }
        if (!data || !Number.isInteger(data.reportCount) || data.reportCount < 0 ||
            !Number.isInteger(data.workpaperCount) || data.workpaperCount < 0) {
          throw new Error('刪除數量尚未確認');
        }
        detail.innerHTML = deleteDetail(target, 'JET 紀錄中仍存在的報告 ' + data.reportCount.toLocaleString() +
          ' 份、工作底稿 ' + data.workpaperCount.toLocaleString() + ' 份。');
      }).catch(function () {
        if (!current()) { return; }
        detail.innerHTML = deleteDetail(target, '報告與工作底稿數量暫時無法確認。可以重試，或在確認刪除範圍後繼續。');
        if (previewRetry) { previewRetry.hidden = false; }
      });
    }

    if (previewRetry) { previewRetry.addEventListener('click', function () { loadDeletePreview(pendingDelete); }); }

    function openCreate() {
      // 清空殘留的舊案件狀態，確保建立案件步驟是全新表單。
      Ui.invalidateProjectListRequests();
      Store.resetWorkflow();
      Store.setView('workflow');
    }

    function restoreProjectDeleteFocus(target) {
      Focus.defer(function () {
        if (target.trigger && target.trigger.isConnected) { return target.trigger; }
        return container.querySelector(
          '[data-action="picker-delete"][data-project-id="' + CSS.escape(target.id) + '"]') ||
          container.querySelector('[data-action="picker-open"]') ||
          container.querySelector('[data-action="picker-new"]');
      });
    }

    function closeModal(restoreFocus) {
      var target = pendingDelete;
      pendingDelete = null;
      if (modal) { modal.hidden = true; }
      if (restoreFocus && target) { restoreProjectDeleteFocus(target); }
    }

    // 開案與新增皆是原生 button；鍵盤 Enter／Space 交給瀏覽器，不再手寫第二次觸發。
    container.querySelectorAll('[data-action="picker-open"], [data-action="picker-new"]').forEach(function (row) {
      var isNew = row.getAttribute('data-action') === 'picker-new';
      var activate = isNew
        ? openCreate
        : function () {
          // 資料庫種類只讓後端判斷本機找不到資料夾時要不要查線上登錄。
          Ui.openProject(row.getAttribute('data-project-id'), row.getAttribute('data-project-provider'));
        };

      row.addEventListener('click', activate);
    });

    // 刪除 icon：不冒泡觸發整列開啟，改開確認框。
    container.querySelectorAll('[data-action="picker-delete"]').forEach(function (btn) {
      btn.addEventListener('click', function (event) {
        event.stopPropagation();
        pendingDelete = {
          id: btn.getAttribute('data-project-id'),
          name: btn.getAttribute('data-project-name'),
          provider: btn.getAttribute('data-project-provider'),
          trigger: btn
        };
        if (modal) {
          modal.querySelector('[data-bind="delete-modal-name"]').textContent = pendingDelete.name;
          modal.hidden = false;
          loadDeletePreview(pendingDelete);
          Focus.move(modal.querySelector('button[data-action="modal-cancel"]'));
        }
      });
    });

    container.querySelector('[data-action="picker-refresh-local"]').addEventListener('click', Ui.loadProjects);
    container.querySelector('[data-action="picker-sync-online"]').addEventListener('click', Ui.syncOnlineProjects);

    var copyError = container.querySelector('[data-action="picker-copy-error"]');
    if (copyError) {
      copyError.addEventListener('click', function () {
        var current = Store.getState();
        var context = current.pickerFeedbackContext || {};
        var detail = [
          current.pickerFeedback || '',
          context.errorCode ? 'errorCode=' + context.errorCode : '',
          context.correlationId ? 'correlationId=' + context.correlationId : ''
        ].filter(Boolean).join('\r\n');
        writeClipboardText(detail).then(function () {
          copyError.textContent = '已複製';
        }, function () {
          copyError.textContent = '複製失敗';
        });
      });
    }

    var exportSupport = container.querySelector('[data-action="picker-support-export"]');
    if (exportSupport) {
      exportSupport.addEventListener('click', function () {
        var current = Store.getState();
        var context = current.pickerFeedbackContext;
        if (!context || !context.projectId) { return; }
        var originalMessage = current.pickerFeedback;
        Ui.run('輸出支援日誌', function () {
          return global.JetApi.supportLogExport({
            projectId: context.projectId,
            correlationId: context.correlationId || null
          }).then(function (data) {
            Store.setPickerFeedback(
              originalMessage,
              Object.assign({}, context, { exportedPath: data.filePath }));
          });
        }, {
          onError: function (message) { Store.setPickerFeedback(message, context); }
        });
      });
    }

    // Debug 組建：把含 SQL 與參數的原始診斷日誌連同這次失敗的 correlation 寫到同一個案件目錄。
    var exportDevLog = container.querySelector('[data-action="picker-dev-log-export"]');
    if (exportDevLog) {
      exportDevLog.addEventListener('click', function () {
        var current = Store.getState();
        var context = current.pickerFeedbackContext;
        if (!context || !context.projectId) { return; }
        var originalMessage = current.pickerFeedback;
        Ui.run('輸出診斷日誌', function () {
          return global.JetApi.devLogExportFile({
            projectId: context.projectId,
            correlationId: context.correlationId || null
          }).then(function (data) {
            var note = data.source === 'ringBuffer' ? '（sink 檔不可讀，內容為 ring buffer 快照）' : '';
            Store.setPickerFeedback(
              originalMessage,
              Object.assign({}, context, { devExportedPath: data.filePath + note }));
          });
        }, {
          onError: function (message) { Store.setPickerFeedback(message, context); }
        });
      });
    }

    if (modal) {
      modal.querySelectorAll('[data-action="modal-cancel"]').forEach(function (el) {
        el.addEventListener('click', function () { closeModal(true); });
      });

      modal.addEventListener('keydown', function (event) {
        if (modal.hidden) { return; }
        if (event.key === 'Escape') {
          event.preventDefault();
          closeModal(true);
          return;
        }
        Focus.trapDialogTab(modal, event);
      });

      modal.querySelector('[data-action="modal-confirm"]').addEventListener('click', function () {
        if (!pendingDelete) { return; }
        var target = pendingDelete;
        closeModal(false);
        Store.setPickerFeedback(null);
        Ui.run('刪除專案', function () {
          return global.JetApi.projectDelete({ projectId: target.id }).then(function (data) {
            Store.addMessage('已刪除專案「' + target.name + '」。', 'info');
            // 本機資料夾清理失敗時後端仍回 ok，附 message（DB 刪除已成功）——照實提示，不當作刪除失敗。
            if (data && data.message) { Store.addMessage(data.message, 'warn'); }
          });
        }, {
          refresh: Ui.loadProjects,
          refreshWhen: 'success',
          onError: function (message, error) {
            Store.setPickerFeedback(message, {
              projectId: target.id,
              errorCode: error && error.code,
              correlationId: error && error.correlationId
            });
          }
        }).then(function () {
          restoreProjectDeleteFocus(target);
        });
      });
    }
  }

  /* ---- 流程步驟導航 ------------------------------------------------------ */

  // 切換步驟：新的當前步驟預設展開（不沿用上一個步驟的手動收合狀態）。
  // 放行仍由 Ui.gotoStep 依既有進入條件判斷；此處不自行放行。
  function navigateToStep(index) {
    Store.setStepFlowCollapsed(false);
    Ui.gotoStep(index);
  }

  // 左欄目錄：六步清單（狀態符號＋名稱）＋進度列。狀態全由 ui-core 的進入條件推導。
  function renderStepNav(state, steps) {
    var nav = Ui.$('step-nav');
    if (!nav) { return; }

    // 目錄狀態隨資料版本與當前步驟變動；純訊息／busy 不影響。
    var key = state.currentStepIndex + '|' + state.contentVersion;
    if (key === lastNavKey) { return; }
    lastNavKey = key;

    var itemsHtml = steps.map(function (step, index) {
      var status = Ui.stepPresentation(state, index).status;
      var reachable = status !== 'locked';
      var symbol;
      if (status === 'done') {
        symbol = '<span class="toc-item__mark toc-item__mark--done">✓</span>';
      } else if (status === 'current') {
        symbol = '<span class="toc-item__mark toc-item__mark--current"><span class="toc-item__dot"></span></span>';
      } else if (status === 'available') {
        symbol = '<span class="toc-item__mark toc-item__mark--available">—</span>';
      } else {
        symbol = '<span class="toc-item__mark toc-item__mark--locked">·</span>';
      }
      return (
        '<button type="button" class="toc-item toc-item--' + status + '" data-step-index="' + index + '"' +
          (reachable ? '' : ' disabled title="' + Ui.esc(Ui.lockedStepTip(state, index)) + '"') +
          (status === 'current' ? ' aria-current="step"' : '') + '>' +
          symbol +
          '<span class="toc-item__label">' + Ui.esc(step.label) + '</span>' +
        '</button>'
      );
    }).join('');

    var done = Ui.doneStepCount(state);
    var total = steps.length;
    var pct = Math.round(done / total * 100);

    nav.innerHTML =
      '<div class="toc-list">' + itemsHtml + '</div>' +
      '<div class="toc-progress">進度 ' + done + '/' + total +
        '<span class="toc-progress__track">' +
          '<span class="toc-progress__fill" style="width:' + pct + '%"></span>' +
        '</span>' +
      '</div>';

    nav.querySelectorAll('.toc-item[data-step-index]').forEach(function (btn) {
      if (btn.disabled) { return; }
      var index = parseInt(btn.getAttribute('data-step-index'), 10);
      btn.addEventListener('click', function () { navigateToStep(index); });
    });
  }

  /* ---- 中央內容區：由上而下步驟文件流 ------------------------------------- */

  function fmtNum(n) {
    return (n == null || isNaN(n)) ? '—' : Number(n).toLocaleString('en-US');
  }

  // 查核期間文字（缺值合理降級，不造假）。
  function auditPeriodText(state) {
    var p = state.project;
    if (!p || (!p.periodStart && !p.periodEnd)) { return ''; }
    return (p.periodStart || '—') + ' ～ ' + (p.periodEnd || '—');
  }

  // 每步收合行的一句摘要：純從既有 state 欄位讀，不在前端計算業務數值。
  function stepSummary(state, index, status) {
    var id = Store.STEPS[index].id;
    var imp = state.importState;

    if (id === 'create') {
      if (!state.project) { return '輸入案件基本資料'; }
      return '案件基本資料已建立';
    }

    if (id === 'import') {
      var seg = [];
      if (imp.gl) { seg.push('總帳明細 ' + fmtNum(imp.gl.rowCount) + ' 筆'); }
      if (imp.tb) { seg.push('試算表 ' + fmtNum(imp.tb.rowCount) + ' 筆'); }
      return seg.length ? seg.join(' / ') : '匯入總帳明細與試算表';
    }

    if (id === 'mapping') {
      return ['gl', 'tb'].map(function (kind) {
        var label = kind.toUpperCase();
        if (state.mapping[kind].committed) { return label + ' 已確認配對'; }
        return label + (imp[kind] ? ' 待確認配對' : ' 尚未匯入');
      }).join(' / ');
    }

    if (id === 'validate') {
      return state.lastRuns.validate ? '已完成資料驗證' : '準備執行資料驗證';
    }

    if (id === 'filter') {
      var n = state.filter.savedScenarios.length;
      return n > 0 ? '已儲存 ' + n + ' 個篩選情境' : '設定並儲存篩選情境';
    }

    if (id === 'export') {
      // 和左側進度、第六步的「完成」用同一個判斷：只有目前版本的工作底稿才算已匯出。
      var workpaper = Ui.currentWorkpaperArtifact(state);
      if (!workpaper) { return '匯出工作底稿'; }
      var exportedAt = workpaper.generatedUtc && !isNaN(new Date(workpaper.generatedUtc).getTime())
        ? Ui.formatDateTime(workpaper.generatedUtc) : '';
      return exportedAt ? '已匯出工作底稿，' + exportedAt : '已匯出工作底稿';
    }

    return '';
  }

  // 當前步驟的進入條件提示：鏡像下一步的進入條件（純呈現），最後一步不顯示。
  function entryConditionHtml(state, index) {
    if (index >= Store.STEPS.length - 1) { return ''; }
    var gate = Ui.stepGate(state, index + 1);
    if (gate.ok) { return ''; }
    return '<span class="stepflow-item__cond">尚缺：' + Ui.esc(gate.missing.join('、')) + '</span>';
  }

  function stepSectionHtml(state, step, index) {
    var pres = Ui.stepPresentation(state, index);
    var label = Ui.esc(step.label);

    if (pres.status === 'current') {
      // 手動收合：一行摘要；否則展開，body 交給既有步驟渲染器。
      if (state.stepFlowCollapsed) {
        return (
          '<div class="stepflow-item stepflow-item--current stepflow-item--collapsed" ' +
            'data-step-index="' + index + '" data-step-toggle role="button" tabindex="0">' +
            '<span class="stepflow-item__caret">▸</span>' +
            '<span class="stepflow-item__name">' + label + '</span>' +
            '<span class="stepflow-item__flag">進行中</span>' +
            '<span class="stepflow-item__summary">' + Ui.esc(stepSummary(state, index, 'current')) + '</span>' +
          '</div>'
        );
      }
      return (
        '<div class="stepflow-item stepflow-item--current">' +
          '<div class="stepflow-item__head" data-step-index="' + index + '" data-step-toggle role="button" tabindex="0">' +
            '<span class="stepflow-item__caret stepflow-item__caret--current">▾</span>' +
            '<h3 class="stepflow-item__title" data-bind="current-step-title" data-step-index="' + index +
              '" tabindex="-1">' + label + '</h3>' +
            '<span class="stepflow-item__status"><span class="stepflow-item__flag">進行中</span>' +
              entryConditionHtml(state, index) + '</span>' +
          '</div>' +
          '<div class="stepflow-item__body"></div>' +
        '</div>'
      );
    }

    if (pres.status === 'done') {
      return (
        '<div class="stepflow-item stepflow-item--done" data-step-index="' + index + '" data-step-nav role="button" tabindex="0">' +
          '<span class="stepflow-item__caret">▸</span>' +
          '<span class="stepflow-item__mark stepflow-item__mark--done">✓</span>' +
          '<span class="stepflow-item__name stepflow-item__name--done">' + label + '</span>' +
          '<span class="stepflow-item__summary">' + Ui.esc(stepSummary(state, index, 'done')) + '</span>' +
        '</div>'
      );
    }

    if (pres.status === 'available') {
      return (
        '<div class="stepflow-item stepflow-item--available" data-step-index="' + index + '" data-step-nav role="button" tabindex="0">' +
          '<span class="stepflow-item__caret">▸</span>' +
          '<span class="stepflow-item__mark stepflow-item__mark--available">—</span>' +
          '<span class="stepflow-item__name stepflow-item__name--available">' + label + '</span>' +
          '<span class="stepflow-item__summary">' + Ui.esc(stepSummary(state, index, 'available')) + '</span>' +
        '</div>'
      );
    }

    // locked：淡化行＋鎖定原因；不可點擊。
    return (
      '<div class="stepflow-item stepflow-item--locked">' +
        '<span class="stepflow-item__spacer-caret"></span>' +
        '<span class="stepflow-item__spacer-mark"></span>' +
        '<span class="stepflow-item__name stepflow-item__name--locked">' + label + '</span>' +
        '<span class="stepflow-item__reason">' + Ui.esc(pres.lockedReason) + '</span>' +
      '</div>'
    );
  }

  function bindStepFlow(container) {
    container.querySelectorAll('[data-step-nav]').forEach(function (el) {
      var index = parseInt(el.getAttribute('data-step-index'), 10);
      var activate = function () { navigateToStep(index); };
      el.addEventListener('click', activate);
      el.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); activate(); }
      });
    });

    container.querySelectorAll('[data-step-toggle]').forEach(function (el) {
      var toggle = function () { Store.setStepFlowCollapsed(!Store.getState().stepFlowCollapsed); };
      el.addEventListener('click', toggle);
      el.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); }
      });
    });
  }

  function focusCurrentStepHeading(container, state, announce) {
    var stepIndex = state.currentStepIndex;
    // 從點擊導航時，瀏覽器會在 click handler 結束後完成原觸發鈕的預設焦點處理。
    // 延後一個 task 並屆時重找當前標題，避免舊點擊或步驟內背景預覽重繪覆寫焦點。
    Focus.defer(function () {
      var latest = Store.getState();
      if (latest.view !== 'workflow' || latest.currentStepIndex !== stepIndex) { return null; }
      var heading = container.querySelector(
        '[data-bind="current-step-title"][data-step-index="' + stepIndex + '"]');
      if (announce && heading) {
        var announcer = Ui.$('step-announcer');
        if (announcer) { announcer.textContent = '目前步驟：' + Store.STEPS[stepIndex].label; }
      }
      return heading;
    });
  }

  // 「凡寫入即 bump 重繪」慣例（state.js）的框架層配套：只有顯式標記唯一 data-focus-key 的控制項
  // 才還原焦點，避免以通用 data-bind/data-action 猜測而把焦點送到同名的錯誤元素。
  function captureContentFocusSelector(container) {
    var active = document.activeElement;
    if (!active || !container.contains(active)) { return null; }
    var key = active.getAttribute('data-focus-key');
    return key ? '[data-focus-key="' + global.CSS.escape(key) + '"]' : null;
  }

  function captureContentTextSelection(container) {
    var active = document.activeElement;
    if (!active || !container.contains(active) || typeof active.selectionStart !== 'number' ||
        typeof active.selectionEnd !== 'number' || typeof active.setSelectionRange !== 'function') { return null; }
    return { value: active.value, start: active.selectionStart, end: active.selectionEnd, direction: active.selectionDirection };
  }

  function restoreContentTextSelection(element, selection) {
    // date、number、select 等控制項不支援文字選取。值已改變時也不沿用舊字串的選取範圍。
    if (element && selection && element.value === selection.value &&
        typeof element.selectionStart === 'number' && typeof element.selectionEnd === 'number' &&
        typeof element.setSelectionRange === 'function') {
      element.setSelectionRange(selection.start, selection.end, selection.direction || 'none');
    }
  }

  function captureContentScrollPositions(container) {
    var positions = {};
    container.querySelectorAll('[data-preserve-scroll]').forEach(function (element) {
      if (element.scrollLeft || element.scrollTop) {
        positions[element.getAttribute('data-preserve-scroll')] = {
          left: element.scrollLeft,
          top: element.scrollTop
        };
      }
    });
    return positions;
  }

  function restoreContentScrollPositions(container, positions) {
    Object.keys(positions).forEach(function (key) {
      var element = container.querySelector('[data-preserve-scroll="' + global.CSS.escape(key) + '"]');
      if (element) {
        element.scrollLeft = positions[key].left;
        element.scrollTop = positions[key].top;
      }
    });
  }

  function bindContentComposition(container) {
    if (contentComposition && contentComposition.container === container) { return contentComposition; }
    var composition = { container: container, target: null };
    contentComposition = composition;
    container.addEventListener('compositionstart', function (event) {
      var state = Store.getState();
      composition.target = event.target;
      composition.project = state.project;
      composition.view = state.view;
      composition.step = state.currentStepIndex;
    });
    container.addEventListener('compositionend', function (event) {
      if (composition.target !== event.target) { return; }
      composition.target = null;
      // 等最後一個 input 事件保存文字，再呈現組字期間收到的背景結果。
      global.setTimeout(render, 0);
    });
    return composition;
  }

  function renderContent(state, current) {
    var container = Ui.$('content');
    if (!container) { return; }
    if (current.id === 'create' && Ui.refreshCreateIdentity) { Ui.refreshCreateIdentity(container, state); }
    if (current.id === 'create' && Ui.refreshCreateDatabaseAvailability) { Ui.refreshCreateDatabaseAvailability(container, state); }
    var composition = bindContentComposition(container);
    if (composition.target) {
      if (composition.target.isConnected && document.activeElement === composition.target && container.contains(composition.target) &&
          composition.project === state.project && composition.view === state.view &&
          composition.step === state.currentStepIndex) { return; }
      composition.target = null;
    }

    // (view, step, contentVersion, 展開/收合)：資料更新或收合切換才重建，
    // 純訊息或 busy 變動不會（保住展開步驟渲染器已綁定的事件與輸入焦點）。
    // 身分晚到由上方只更新建立案件的操作人員提示，不能清掉已輸入的案件資料。
    var key = state.view + '|' + current.id + '|' + state.contentVersion + '|' +
      (state.stepFlowCollapsed ? 'c' : 'e');
    if (key === lastContentKey) { return; }
    lastContentKey = key;

    // 當步驟內的背景預覽等純資料更新重建 content 時，若焦點正在當前標題，
    // 重建後回到新節點；不重複發送「已切步」宣告。
    var activeElement = document.activeElement;
    var preserveStepHeadingFocus = !!activeElement && activeElement.matches(
      '[data-bind="current-step-title"][data-step-index="' + state.currentStepIndex + '"]');
    var focusSelector = captureContentFocusSelector(container);
    var textSelection = focusSelector ? captureContentTextSelection(container) : null;
    var scrollPositions = captureContentScrollPositions(container);
    var contentScrollTop = container.scrollTop;
    var contentScrollLeft = container.scrollLeft;

    var html = '<div class="stepflow">';
    Store.STEPS.forEach(function (step, index) {
      html += stepSectionHtml(state, step, index);
    });
    html += '</div>';
    container.innerHTML = html;

    // 進入新步驟時把中欄捲回頂部：.content 是垂直捲動容器（overflow-y:auto），重建 innerHTML
    // 不會重置它的 scrollTop——上一步在頁尾按「下一步」殘留的捲動位置會讓長步驟（如資料驗證）
    // 一進來就停在頁尾。只在 (view, step) 真的改變時觸發；同一步驟內的資料更新／收合重繪不動捲軸，
    // 也不干涉步驟內既有的橫向捲動保留（mapping 對照表格）。
    var stepScrollKey = state.view + '|' + current.id;
    var stepChanged = stepScrollKey !== lastStepScrollKey;
    if (stepChanged) {
      lastStepScrollKey = stepScrollKey;
      container.scrollTop = 0;
    }

    // 展開的當前步驟：把 body 元素交給既有已註冊渲染器（等同過去傳 content 容器）。
    if (!state.stepFlowCollapsed) {
      var body = container.querySelector('.stepflow-item--current .stepflow-item__body');
      if (body) { Ui.renderStep(current.id, body, state); }
    }

    // 同一步驟內的資料更新重繪：還原標記容器的捲動與輸入焦點；跨步切換不還原
    //（新步驟由 focusCurrentStepHeading 設定起始焦點，捲動另由上方捲頂邏輯處理）。
    if (!stepChanged) {
      restoreContentScrollPositions(container, scrollPositions);
      if (focusSelector && !preserveStepHeadingFocus) {
        var focused = container.querySelector(focusSelector);
        focusElement(focused);
        restoreContentTextSelection(focused, textSelection);
      }
      // innerHTML 暫時縮短內容時，瀏覽器可能先夾縮外層 scrollTop；focus 也可能捲動祖先。
      // 等正式步驟與焦點都恢復後，再還原內外捲軸。
      restoreContentScrollPositions(container, scrollPositions);
      container.scrollTop = contentScrollTop;
      container.scrollLeft = contentScrollLeft;
    }

    bindStepFlow(container);
    if (stepChanged || preserveStepHeadingFocus) {
      focusCurrentStepHeading(container, state, stepChanged);
    }
  }

  /* ---- 訊息區 -------------------------------------------------------------- */

  function renderMessages(state) {
    renderMessagesPanel(state);

    var list = Ui.$('message-list');
    if (!list) { return; }

    if (state.messages.length === 0) {
      if (!messagesEmptyShown) {
        list.innerHTML = '';
        var empty = document.createElement('li');
        empty.className = 'messages__empty';
        empty.textContent = '目前沒有訊息。';
        list.appendChild(empty);
        messagesEmptyShown = true;
      }
      return;
    }

    if (messagesEmptyShown) {
      list.innerHTML = '';
      messagesEmptyShown = false;
    }

    var fresh = [];
    for (var i = 0; i < state.messages.length; i++) {
      if (state.messages[i].id > lastMessageId) {
        fresh.push(state.messages[i]);
      } else {
        break;
      }
    }

    for (var j = fresh.length - 1; j >= 0; j--) {
      list.insertBefore(buildMessage(fresh[j]), list.firstChild);
    }

    if (fresh.length > 0) {
      lastMessageId = state.messages[0].id;
    }

    while (list.children.length > state.messages.length) {
      list.removeChild(list.lastChild);
    }
  }

  function buildMessage(msg) {
    var li = document.createElement('li');
    li.className = 'messages__item messages__item--' + msg.level;
    li.innerHTML =
      '<span class="messages__time">' + msg.time + '</span>' +
      '<span class="messages__text">' + Ui.esc(msg.text) + '</span>';
    return li;
  }

  // 收合/展開與未讀徽章（收合期間有 warn 訊息時徽章轉警示色）
  function renderMessagesPanel(state) {
    var panel = document.querySelector('[data-bind="messages-panel"]');
    if (!panel) { return; }

    panel.classList.toggle('messages--collapsed', !state.messagesPanelOpen);

    var rail = panel.querySelector('.messages__rail');
    if (rail) {
      rail.setAttribute('aria-expanded', state.messagesPanelOpen ? 'true' : 'false');
    }

    var badge = Ui.$('message-badge');
    if (badge) {
      badge.hidden = state.messagesUnseen === 0;
      badge.textContent = state.messagesUnseen > 99 ? '99+' : String(state.messagesUnseen);

      var hasUnseenWarn = false;
      for (var i = 0; i < Math.min(state.messagesUnseen, state.messages.length); i++) {
        if (state.messages[i].level === 'warn') { hasUnseenWarn = true; break; }
      }
      badge.classList.toggle('messages__badge--warn', hasUnseenWarn);
    }
  }

  /* ---- 流程總覽（4b modal）：純檢視補充視窗 -----------------------------------
     資料全讀既有 state；步驟狀態／閘門一律取自 ui-core。六階段只在 modal 內切換
     執行紀錄，不改 currentStepIndex、不送 project.saveProgress；流程切換留在主畫面。 */

  var lastOverviewKey = null;
  // 集中度分析的分頁選擇：純 UI 狀態（不進 state、不進後端 payload），切換只重繪卡片。
  var overviewConcentrationTab = 'preparers';
  var overviewExpandedSections = {};
  var overviewSelectedStepIndex = null;
  var overviewReturnFocus = null;
  var overviewWasOpen = false;

  // 開關與內容渲染：關閉即隱藏並讓 key 失效（下次開啟一律重讀當下 state 重繪）。
  function renderOverview(state) {
    var modal = document.querySelector('.overview-modal');
    if (!modal) { return; }

    if (!state.overviewOpen) {
      global.JetOverviewBi.dispose();
      modal.hidden = true;
      lastOverviewKey = null;
      overviewExpandedSections = {};
      overviewConcentrationTab = 'preparers';
      overviewSelectedStepIndex = null;
      if (overviewWasOpen && overviewReturnFocus && document.contains(overviewReturnFocus)) {
        overviewReturnFocus.focus();
      }
      overviewReturnFocus = null;
      overviewWasOpen = false;
      return;
    }

    modal.hidden = false;
    var firstOpen = !overviewWasOpen;
    overviewWasOpen = true;
    if (overviewSelectedStepIndex == null ||
        overviewSelectedStepIndex < 0 ||
        overviewSelectedStepIndex >= Store.STEPS.length) {
      overviewSelectedStepIndex = state.currentStepIndex;
    }
    // 開啟中：資料版本／當前步驟改變才重繪；最近訊息不再複製進總覽。
    var key = state.contentVersion + '|' + state.currentStepIndex;
    if (key === lastOverviewKey) { return; }
    lastOverviewKey = key;

    renderOverviewCard(state);
    if (firstOpen) {
      var initialFocus = modal.querySelector('[data-action="overview-close"]');
      if (initialFocus) { initialFocus.focus(); }
    }
  }

  // 卡片內容重繪（開啟時的一般渲染，以及分頁切換這類純 UI 變動的即時重繪共用）。
  function renderOverviewCard(state) {
    var card = document.querySelector('.overview-modal__card');
    if (!card) { return; }
    global.JetOverviewBi.dispose();
    card.innerHTML = overviewHtml(state);
    restoreOverviewAnalysisState(card);
    bindOverview(card);
    global.JetOverviewBi.mount(
      card,
      state.lastRuns.validate,
      state.lastRuns.prescreen,
      overviewConcentrationTab);
  }

  function overviewHtml(state) {
    return (
      '<div class="overview">' +
        overviewHeadHtml(state) +
        overviewPipelineHtml(state) +
        overviewPopulationHtml(state) +
        overviewAnalysisHtml(state) +
        overviewTechnicalHtml(state) +
        overviewDisclaimerHtml(state) +
      '</div>'
    );
  }

  // 抬頭只保留審計員當下辨識案件所需的案件名稱與期間；執行識別移入「技術資訊」。
  // 1) 抬頭：mono eyebrow ＋ serif 案件標題 ＋ 基準行 ＋ ✕；底部 2px 實線（CSS）。
  // 標題放審計員最先要認的名字：有客戶名稱用客戶名稱，沒有就用案件名稱；「分錄測試」放進小標，
  // 不用破折號接在名字後面。標題已是案件名稱時，基準行不再重複寫一次。
  function overviewHeadHtml(state) {
    var eyebrow = '分錄測試流程總覽';
    var caseName = state.caseId || '';
    var title = state.project
      ? (state.caseClient || caseName || '目前案件')
      : '尚未選擇案件';
    var showCaseName = !!state.project && !!caseName && caseName !== title;
    var period = auditPeriodText(state) || '尚未設定';
    return (
      '<div class="overview__head">' +
        '<div>' +
          '<span class="overview__eyebrow">' + Ui.esc(eyebrow) + '</span>' +
          '<h3 class="overview__title" id="overview-title">' + Ui.esc(title) + '</h3>' +
          '<span class="overview__baseline">' +
            (showCaseName
              ? '<span data-overview-fact-key="project.case-id">案件 ' + Ui.esc(caseName) + '</span>' +
                '<span aria-hidden="true">，</span>'
              : '') +
            '<span data-overview-fact-key="project.audit-period">查核期間 ' + Ui.esc(period) + '</span>' +
          '</span>' +
        '</div>' +
        '<button type="button" class="overview__close" data-action="overview-close" ' +
          'title="關閉流程總覽" aria-label="關閉流程總覽">✕</button>' +
      '</div>'
    );
  }

  /* 分錄測試範圍組成：一欄一個明確事實，四欄皆直接鏡射 validate.run 的
     stats 欄位，前端不做任何聚合或換算。未執行驗證時整段以空狀態取代，
     以骨架標籤預示版位，不塞提示文字、不顯示 0 冒充「已核對為零」。
     完整的母體完整性核對清單仍只在步驟「資料驗證與測試」呈現——那裡是唯一權威判定面；
     總覽只鏡射目前有效的母體基礎，不複製判定或流程操作。 */
  function overviewPopulationEmptyHtml(state) {
    return (
      '<div class="overview__population overview__population--empty">' +
        '<span class="overview-pop__icon" aria-hidden="true">□</span>' +
        '<h4 class="overview-pop__empty-title">尚無分錄統計</h4>' +
        '<p class="overview-pop__empty-body">完成匯入、欄位配對與資料驗證後，' +
          '這裡會顯示分錄統計與分析圖表。</p>' +
      '</div>'
    );
  }

  function overviewPopKpiHtml(key, label, value, note) {
    return (
      '<div class="overview-pop__kpi" data-overview-fact-key="' + Ui.esc(key) + '">' +
        '<span class="overview-pop__kpi-label">' + Ui.esc(label) + '</span>' +
        '<span class="overview-pop__kpi-value">' + Ui.esc(value) + '</span>' +
        (note ? '<span class="overview-pop__kpi-note">' + Ui.esc(note) + '</span>' : '') +
      '</div>'
    );
  }

  function overviewPopulationHtml(state) {
    var validate = state.lastRuns.validate;
    var stats = validate && validate.stats ? validate.stats : null;
    if (!stats) { return overviewPopulationEmptyHtml(state); }

    return (
      '<div class="overview__population">' +
        '<div class="overview-pop__head">' +
          '<span class="overview__col-eyebrow">分錄測試範圍</span>' +
        '</div>' +
        '<div class="overview-pop__grid">' +
          overviewPopKpiHtml('validation.stats.gl-row-count', '納入測試的分錄', fmtNum(stats.glRowCount), '') +
          overviewPopKpiHtml('validation.stats.voucher-count', '傳票數', fmtNum(stats.voucherCount), '') +
          overviewPopKpiHtml('validation.stats.total-debit', '借方合計金額', Ui.money(stats.totalDebit), '') +
          overviewPopKpiHtml('validation.stats.total-credit', '貸方合計金額', Ui.money(stats.totalCredit),
            '借貸淨額 ' + Ui.moneyDifference(stats.net)) +
        '</div>' +
      '</div>'
    );
  }

  /* 預篩選規則全期命中分布（S2b 降級版）：直接鏡射 prescreen.run 的 rulePeriod。
     母體尚未就緒時沿用上方單一空狀態；舊 prescreen summary 的降級由 renderer 負責。 */
  function overviewRulePeriodHtml(state) {
    var validate = state.lastRuns.validate;
    if (!validate || !validate.stats) { return ''; }
    return global.JetOverviewBi.rulePeriodHtml(state.lastRuns.prescreen);
  }

  /* 分錄金額級距與累積分布：count／ECDF 直接鏡射
     validate.run.amountDistribution。母體未就緒時沿用上方單一空狀態；
     舊 validation summary 的降級由 renderer 負責。 */
  function overviewAmountDistributionHtml(state) {
    var validate = state.lastRuns.validate;
    if (!validate || !validate.stats) { return ''; }
    return global.JetOverviewBi.amountDistributionHtml(validate);
  }

  /* 集中度分析：全部數字取自 prescreen.run 回應的 concentration 區塊，
     累積占比／「其他」彙總／前五佔比都由後端算好，前端只鏡射與格式化（渲染在 overview-bi.js）。
     母體尚未就緒時不單獨出現——沿用上方母體概況的單一空狀態，避免同一畫面兩種空狀態。 */
  function overviewConcentrationHtml(state) {
    var validate = state.lastRuns.validate;
    if (!validate || !validate.stats) { return ''; }
    return global.JetOverviewBi.concentrationHtml(state.lastRuns.prescreen, overviewConcentrationTab);
  }

  // 三組分析共享同一個「全查核期間」範圍，個別 accordion 只保留分析名稱。
  function overviewAnalysisHtml(state) {
    var validate = state.lastRuns.validate;
    if (!validate || !validate.stats) { return ''; }
    return (
      '<section class="overview__analyses" aria-labelledby="overview-analysis-title">' +
        '<div class="overview-analyses__head">' +
          '<span class="overview__col-eyebrow" id="overview-analysis-title">分錄分析</span>' +
          '<span class="overview-analyses__scope" data-overview-fact-key="analysis.scope">全查核期間</span>' +
        '</div>' +
        overviewConcentrationHtml(state) +
        overviewAmountDistributionHtml(state) +
        overviewRulePeriodHtml(state) +
      '</section>'
    );
  }

  // 只保留會改變審計解讀的必要提醒。
  function overviewDisclaimerHtml(state) {
    var positioning = Ui.prescreenPositioningCopy(state.lastRuns.prescreen);
    return (
      '<p class="overview__disclaimer">' +
        Ui.esc(positioning.overviewGuidance) +
        ' 列在「不適用規則」的條件因缺少所需資料或設定而沒有執行，不代表沒有符合的分錄。' +
      '</p>'
    );
  }

  function overviewRunId(run) {
    return run && run.resultRef ? run.resultRef.runId : null;
  }

  // 與第六步共用 Ui.formatDateTime，避免兩處時間格式不一致。
  function overviewWhen(iso) {
    return Ui.formatDateTime(iso);
  }

  function overviewModeLabel(options, value) {
    var matched = (options || []).filter(function (option) { return option.value === value; })[0];
    return matched ? matched.label : '尚未設定';
  }

  function overviewScenarioPositions(state) {
    return (state.filter.savedScenarios || []).map(function (_, index) { return index + 1; });
  }

  function overviewCurrentCriteriaArtifact(state) {
    var validationRunId = overviewRunId(state.lastRuns.validate);
    var revision = state.filterResultRef ? state.filterResultRef.revision : null;
    if (!validationRunId || !revision) { return null; }
    return Ui.findCurrentReportArtifact(state, 'criteriaSelectionReport', {
      validationRunId: validationRunId,
      scenarioRevision: revision,
      scenarioPositions: overviewScenarioPositions(state)
    });
  }

  // 目前版本的工作底稿與左側進度、第六步完成摘要共用 Ui.currentWorkpaperArtifact，不在總覽另算一份。
  function overviewCurrentWorkpaperArtifact(state) {
    return Ui.currentWorkpaperArtifact(state);
  }

  // 完成判定一律走 Ui.stepComplete：最後階段只以目前版本的工作底稿判定，不能拿歷史底稿冒充完成。
  function overviewStageComplete(state, index) {
    return Ui.stepComplete(state, index);
  }

  // 「完成狀態」與「目前主畫面位置」分開：已完成的階段即使目前正在回看，仍顯示完成。
  function overviewStageStatus(state, index) {
    if (overviewStageComplete(state, index)) { return 'done'; }
    if (Ui.isStepReachable(state, index)) { return 'available'; }
    return 'locked';
  }

  var OVERVIEW_STAGE_STATUS = {
    done: '✓ 完成',
    available: '可處理',
    locked: '等待前置步驟'
  };

  function overviewProgressHtml(state) {
    var statuses = Store.STEPS.map(function (_, index) { return overviewStageStatus(state, index); });
    var done = statuses.filter(function (status) { return status === 'done'; }).length;
    var available = statuses.filter(function (status) { return status === 'available'; }).length;
    var locked = statuses.filter(function (status) { return status === 'locked'; }).length;
    var pct = Math.round(done / Store.STEPS.length * 100);
    var summary = done + ' 個完成';
    if (available) { summary += '，' + available + ' 個可處理'; }
    if (locked) { summary += '，' + locked + ' 個等待前置'; }
    return (
      '<div class="overview__progress" data-overview-fact-key="workflow.aggregate">' +
        '<span class="overview__progress-left">' +
          '<span class="overview__progress-count">案件進度</span>' +
          '<span class="overview__progress-track" role="progressbar" aria-label="案件流程完成度" ' +
            'aria-valuemin="0" aria-valuemax="' + Store.STEPS.length + '" aria-valuenow="' + done + '">' +
            '<span class="overview__progress-fill" style="width:' + pct + '%"></span>' +
          '</span>' +
        '</span>' +
        '<span class="overview__progress-stat">' + Ui.esc(summary) + '</span>' +
      '</div>'
    );
  }

  function overviewPipelineHtml(state) {
    var stages = Store.STEPS.map(function (step, index) {
      var status = overviewStageStatus(state, index);
      var selected = index === overviewSelectedStepIndex;
      var currentLocation = index === state.currentStepIndex;
      return (
        '<button type="button" class="overview-stage overview-stage--' + status +
          (selected ? ' overview-stage--selected' : '') + '" data-overview-review="' + index + '" ' +
          'id="overview-stage-tab-' + index + '" role="tab" aria-controls="overview-step-detail" ' +
          'aria-selected="' + (selected ? 'true' : 'false') + '" tabindex="' + (selected ? '0' : '-1') + '">' +
          '<span class="overview-stage__top">' +
            '<span class="overview-stage__status" data-overview-fact-key="workflow.stage.' + index +
              '.status">' + OVERVIEW_STAGE_STATUS[status] + '</span>' +
          '</span>' +
          '<span class="overview-stage__name">' + Ui.esc(step.label) + '</span>' +
          (currentLocation
            ? '<span class="overview-stage__location" data-overview-fact-key="workflow.location">目前主畫面</span>'
            : '') +
        '</button>'
      );
    }).join('');
    return overviewProgressHtml(state) +
      '<div class="overview__pipeline" role="tablist" aria-label="六階段執行狀態">' + stages + '</div>' +
      overviewStepDetailHtml(state);
  }

  // 技術資訊：保留可追溯資料，但預設收合，不與審計員的當前任務競爭注意力。
  function overviewKvRow(label, value, mono, key) {
    return (
      '<div class="overview-kv" data-overview-fact-key="' + Ui.esc(key) + '">' +
        '<span class="overview-kv__label">' + Ui.esc(label) + '</span>' +
        '<span class="overview-kv__value' + (mono ? ' overview-kv__value--mono' : '') + '">' +
          Ui.esc(value) + '</span>' +
      '</div>'
    );
  }

  function overviewTechnicalHtml(state) {
    var provider = state.project ? providerText(state.project.databaseProvider) : '—';
    var ref = (state.lastRuns.prescreen && state.lastRuns.prescreen.resultRef) ||
      (state.lastRuns.validate && state.lastRuns.validate.resultRef) || null;
    return (
      '<details class="overview__technical">' +
        '<summary>技術資訊</summary>' +
        '<div class="overview__technical-grid">' +
          overviewKvRow('資料庫', provider, false, 'project.database-provider') +
          overviewKvRow('執行識別碼', ref && ref.runId ? ref.runId : '尚未執行', true,
            'execution.run-id') +
          overviewKvRow('規則版本', ref && ref.logicVersion ? ref.logicVersion : '尚未執行', true,
            'execution.logic-version') +
        '</div>' +
      '</details>'
    );
  }

  function overviewFactHtml(key, label, value, note) {
    return (
      '<div class="overview-step-fact" data-overview-fact-key="' + Ui.esc(key) + '">' +
        '<dt class="overview-step-fact__label">' + Ui.esc(label) + '</dt>' +
        '<dd class="overview-step-fact__value">' + Ui.esc(value) +
          (note ? '<span class="overview-step-fact__note">' + Ui.esc(note) + '</span>' : '') +
        '</dd>' +
      '</div>'
    );
  }

  function overviewImportFact(key, label, item, unit) {
    if (!item) { return overviewFactHtml(key, label, '尚未匯入', ''); }
    var value = '已匯入';
    if (item.rowCount != null) { value += '，' + fmtNum(item.rowCount) + ' ' + unit; }
    return overviewFactHtml(key, label, value, item.importedUtc ? overviewWhen(item.importedUtc) : '');
  }

  function overviewStepFactsHtml(state, index) {
    var imp = state.importState;
    if (index === 0) {
      var project = state.project;
      return overviewFactHtml('project.operator', '建案者',
        project && project.operatorId ? project.operatorId : '尚無紀錄', '') +
        overviewFactHtml('project.created-at', '建立時間',
          project ? overviewWhen(project.createdUtc) : '尚無紀錄', '');
    }
    if (index === 1) {
      var supporting = [];
      if (imp.accountMapping) { supporting.push('科目配對'); }
      if (imp.authorizedPreparer) { supporting.push('授權編製人員'); }
      if (imp.calendar && (imp.calendar.calendarImported || imp.calendar.nonWorkingDaysConfigured)) {
        supporting.push('行事曆');
      }
      return overviewImportFact('import.gl.source', '來源 GL 總帳明細', imp.gl, '列') +
        overviewImportFact('import.tb.source', '來源 TB 試算表', imp.tb, '列') +
        overviewFactHtml('import.supporting', '其他資料',
          supporting.length ? supporting.join('、') : '尚未匯入（選用）', '');
    }
    if (index === 2) {
      var glCommitted = state.mapping.gl.committed;
      var tbCommitted = state.mapping.tb.committed;
      return overviewFactHtml('mapping.gl.commit', 'GL 欄位配對',
        glCommitted ? overviewModeLabel(Ui.GL_MODES, glCommitted.mode) : '尚未完成',
        glCommitted && glCommitted.committedUtc ? overviewWhen(glCommitted.committedUtc) : '') +
        overviewFactHtml('mapping.tb.commit', 'TB 欄位配對',
          tbCommitted ? overviewModeLabel(Ui.TB_MODES, tbCommitted.mode) : '尚未完成',
          tbCommitted && tbCommitted.committedUtc ? overviewWhen(tbCommitted.committedUtc) : '');
    }
    if (index === 3) {
      var validation = state.lastRuns.validate;
      var prescreen = state.lastRuns.prescreen;
      var eligibility = Ui.completenessEligibility(validation);
      return overviewFactHtml('validation.current-result', '資料驗證',
        validation ? '目前版本已有結果' : '目前版本需重新執行',
        validation && validation.resultRef ? overviewWhen(validation.resultRef.generatedUtc) : '') +
        overviewFactHtml('validation.completeness', '資料驗證與後續操作', validation
          ? (eligibility.isEligible ? '可執行預篩選或進階條件篩選' : eligibility.reason)
          : '尚無可用結果', eligibility.warning || '') +
        overviewFactHtml('prescreen.current-result', '預篩選',
          prescreen ? '目前版本已有結果' : '選用，尚未執行',
          prescreen && prescreen.resultRef ? overviewWhen(prescreen.resultRef.generatedUtc) : '');
    }
    if (index === 4) {
      var criteria = overviewCurrentCriteriaArtifact(state);
      var resultRef = state.filterResultRef;
      return overviewFactHtml('filter.saved-scenarios', '已儲存情境', state.filter.savedScenarios.length
        ? state.filter.savedScenarios.length + ' 個'
        : '尚未儲存', '') +
        overviewFactHtml('filter.population-scope', '分錄測試範圍',
          resultRef && resultRef.populationScope === 'auditPeriod'
          ? '查核期間'
          : '目前版本尚未儲存', '') +
        overviewFactHtml('filter.current-result', '條件篩選',
          resultRef ? '目前版本已有結果' : '目前版本需重新執行',
          resultRef ? overviewWhen(resultRef.generatedUtc) : '') +
        overviewFactHtml('filter.criteria-report', '條件篩選報告',
          criteria ? criteria.fileName : '目前版本尚未產生',
          criteria ? overviewWhen(criteria.generatedUtc) : '');
    }

    var workpaper = overviewCurrentWorkpaperArtifact(state);
    var positions = workpaper && workpaper.sourceRef && Array.isArray(workpaper.sourceRef.scenarioPositions)
      ? workpaper.sourceRef.scenarioPositions.length
      : null;
    return overviewFactHtml('export.working-paper', '工作底稿',
      workpaper ? workpaper.fileName : '目前版本尚未產生',
        workpaper ? overviewWhen(workpaper.generatedUtc) : '') +
      (workpaper && workpaper.bytes != null
        ? overviewFactHtml('export.working-paper-bytes', '檔案大小',
          Ui.fileSizeMbText(workpaper.bytes), '')
        : '') +
      (positions != null
        ? overviewFactHtml('export.scenario-count', '納入情境', positions + ' 個', '')
        : '');
  }

  function overviewStepAttentionHtml(state, index) {
    if (overviewStageComplete(state, index)) { return ''; }
    var missing = [];
    var label = '尚待完成';
    if (overviewStageStatus(state, index) === 'locked') {
      label = '等待前置步驟';
      var lockedReason = Ui.stepPresentation(state, index).lockedReason;
      missing = lockedReason ? [lockedReason] : [];
    } else if (index < Store.STEPS.length - 1) {
      missing = Ui.stepCompletionMissing(state, index);
    } else {
      // 第六步可能只因為已有版本紀錄而能進入；沒有可用情境時先說明情境，再談產生底稿。
      var scenarioMissing = Ui.filterScenarioMissing(state);
      missing = scenarioMissing ? [scenarioMissing] : ['產生目前版本的工作底稿'];
    }
    if (!missing.length) { return ''; }
    return (
      '<div class="overview-step-detail__attention" role="note">' +
        '<span class="overview-step-detail__attention-label">' + label + '</span>' +
        '<p>' + Ui.esc(missing.join('、')) + '</p>' +
      '</div>'
    );
  }

  function overviewStepDetailBodyHtml(state, index) {
    var recordLabel = index === 3 ? '預篩選與執行紀錄' : '所選階段執行紀錄';
    return (
        '<div class="overview-step-detail__head">' +
          '<span class="overview-step-detail__eyebrow">' + recordLabel + '</span>' +
        '</div>' +
        '<dl class="overview-step-detail__facts">' + overviewStepFactsHtml(state, index) + '</dl>' +
        overviewStepAttentionHtml(state, index)
    );
  }

  function overviewStepDetailHtml(state) {
    var index = overviewSelectedStepIndex == null ? state.currentStepIndex : overviewSelectedStepIndex;
    return (
      '<section class="overview-step-detail" ' +
        'id="overview-step-detail" role="tabpanel" tabindex="0" aria-live="polite" ' +
        'aria-labelledby="overview-stage-tab-' + index + '">' +
        overviewStepDetailBodyHtml(state, index) +
      '</section>'
    );
  }

  // WAI-ARIA tabs：選取只更新總覽內詳情；左右鍵、Home、End 同步選取與焦點。
  function bindOverviewStepReview(card) {
    var tabs = Array.prototype.slice.call(card.querySelectorAll('[data-overview-review]'));
    function select(index, moveFocus) {
      overviewSelectedStepIndex = index;
      tabs.forEach(function (tab, tabIndex) {
        var selected = tabIndex === index;
        tab.classList.toggle('overview-stage--selected', selected);
        tab.setAttribute('aria-selected', selected ? 'true' : 'false');
        tab.setAttribute('tabindex', selected ? '0' : '-1');
      });
      var panel = card.querySelector('#overview-step-detail');
      if (panel) {
        var state = Store.getState();
        panel.className = 'overview-step-detail';
        panel.setAttribute('aria-labelledby', 'overview-stage-tab-' + index);
        panel.innerHTML = overviewStepDetailBodyHtml(state, index);
      }
      if (moveFocus && tabs[index]) { tabs[index].focus(); }
    }
    tabs.forEach(function (tab, index) {
      tab.addEventListener('click', function () { select(index, false); });
      tab.addEventListener('keydown', function (event) {
        var next = null;
        if (event.key === 'ArrowLeft') { next = (index + tabs.length - 1) % tabs.length; }
        if (event.key === 'ArrowRight') { next = (index + 1) % tabs.length; }
        if (event.key === 'Home') { next = 0; }
        if (event.key === 'End') { next = tabs.length - 1; }
        if (next == null) { return; }
        event.preventDefault();
        select(next, true);
      });
    });
  }

  function restoreOverviewAnalysisState(card) {
    card.querySelectorAll('[data-overview-analysis]').forEach(function (section) {
      var key = section.getAttribute('data-overview-analysis');
      section.open = overviewExpandedSections[key] === true;
    });
  }

  function bindOverviewDialog() {
    var modal = document.querySelector('.overview-modal');
    if (!modal) { return; }

    var backdrop = modal.querySelector('.overview-modal__backdrop');
    if (backdrop) {
      backdrop.addEventListener('click', function () { Store.setOverviewOpen(false); });
    }

    modal.addEventListener('keydown', function (event) {
      if (!Store.getState().overviewOpen) { return; }
      if (event.key === 'Escape') {
        event.preventDefault();
        Store.setOverviewOpen(false);
        return;
      }
      if (event.key !== 'Tab') { return; }

      var focusable = Array.prototype.slice.call(modal.querySelectorAll(
        'button:not([disabled]), summary, [href], [tabindex]:not([tabindex="-1"])'))
        .filter(function (element) { return !element.hidden && element.getClientRects().length > 0; });
      if (focusable.length === 0) { return; }
      var first = focusable[0];
      var last = focusable[focusable.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    });
  }

  function bindOverviewConcentrationTabs(card) {
    var tabs = Array.prototype.slice.call(card.querySelectorAll('[data-overview-tab]'));
    function select(key) {
      if (key !== 'preparers' && key !== 'accounts') { return; }
      overviewConcentrationTab = key;
      renderOverviewCard(Store.getState());
      var nextTab = document.querySelector('[data-overview-tab="' + key + '"]');
      if (nextTab) { nextTab.focus(); }
    }
    tabs.forEach(function (tab, index) {
      tab.addEventListener('click', function () {
        select(tab.getAttribute('data-overview-tab'));
      });
      tab.addEventListener('keydown', function (event) {
        var next = null;
        if (event.key === 'ArrowLeft') { next = (index + tabs.length - 1) % tabs.length; }
        if (event.key === 'ArrowRight') { next = (index + 1) % tabs.length; }
        if (event.key === 'Home') { next = 0; }
        if (event.key === 'End') { next = tabs.length - 1; }
        if (next == null) { return; }
        event.preventDefault();
        select(tabs[next].getAttribute('data-overview-tab'));
      });
    });
  }

  function bindOverview(card) {
    // 卡片內動態產生的 ✕（靜態背景遮罩已於 init 綁定）。
    var close = card.querySelector('[data-action="overview-close"]');
    if (close) { close.addEventListener('click', function () { Store.setOverviewOpen(false); }); }

    bindOverviewStepReview(card);

    card.querySelectorAll('[data-overview-analysis]').forEach(function (section) {
      section.addEventListener('toggle', function () {
        var key = section.getAttribute('data-overview-analysis');
        overviewExpandedSections[key] = section.open;
        if (section.open) {
          global.JetOverviewBi.mount(
            section,
            Store.getState().lastRuns.validate,
            Store.getState().lastRuns.prescreen,
            overviewConcentrationTab);
        } else {
          global.JetOverviewBi.disposeWithin(section);
        }
      });
    });

    // 集中度分析分頁：純 UI 切換，不重讀後端、不改變任何流程狀態。
    bindOverviewConcentrationTabs(card);

  }

  /* ---- 訊息持久化（log.append） ------------------------------- */

  // 以 id 水位增量持久化：無 active project 的訊息（專案選擇畫面）只推進觀察水位不落庫；
  // fromLog（log.recent 還原的歷史）不回寫，避免重複。append 以單一 promise queue 保序，
  // 只有後端確認後才推進持久化水位；暫時性失敗保留重試。格式拒收已由 dispatcher 寫入支援日誌，跳過該筆。
  var lastObservedMessageId = 0;
  var lastPersistedId = 0;
  var pendingMessageWrites = [];
  var messagePersistQueue = Promise.resolve();

  function flushMessageWrites() {
    messagePersistQueue = messagePersistQueue
      .catch(function () {})
      .then(function drain() {
        if (pendingMessageWrites.length === 0) { return; }

        var pending = pendingMessageWrites[0];
        var state = Store.getState();
        var activeProjectId = state.project && state.project.projectId;
        if (!activeProjectId || activeProjectId !== pending.projectId) {
          pendingMessageWrites.shift();
          return drain();
        }

        return global.JetApi.logAppend({
          level: pending.level,
          text: pending.text
        }).then(function () {
          pendingMessageWrites.shift();
          lastPersistedId = Math.max(lastPersistedId, pending.id);
          return drain();
        }, function (error) {
          if (!error || error.code !== 'invalid_payload') { throw error; }
          // 後端已在回覆錯誤前記錄 action.error；不再用 addMessage 遞迴製造另一筆訊息。
          // 只略過確定無法接受的格式，不把 I/O、忙碌或傳輸失敗當成已保存。
          pendingMessageWrites.shift();
          return drain();
        });
      });
    return messagePersistQueue;
  }

  function persistNewMessages(state) {
    if (state.messages.length === 0) { return; }

    var canPersist = state.project && state.bridgeReady
      && global.JetApi && typeof global.JetApi.logAppend === 'function';

    for (var i = state.messages.length - 1; i >= 0; i--) {
      var msg = state.messages[i];
      if (msg.id <= lastObservedMessageId) { continue; }
      lastObservedMessageId = msg.id;

      if (canPersist && !msg.fromLog) {
        pendingMessageWrites.push({
          id: msg.id,
          projectId: state.project.projectId,
          level: msg.level,
          text: msg.text
        });
      }
    }
    if (pendingMessageWrites.length > 0) {
      flushMessageWrites().catch(function () {});
    }
  }

  // 案件名稱（projectId）是唯一必要識別；案件編號已是選填，留空時複製出的紀錄仍要能辨識案件。
  function operationLogIdentity(project) {
    return {
      projectId: project && project.projectId != null ? project.projectId : '',
      projectCode: project && project.projectCode != null ? project.projectCode : '',
      databaseProvider: project && project.databaseProvider != null ? project.databaseProvider : ''
    };
  }

  function operationLogSessionIsCurrent(project, projectId) {
    var currentProject = Store.getState().project;
    // 同一 projectId 離場後再開案也屬新 session；project.load 發布的物件 identity 必須同時一致。
    return projectId != null && projectId !== '' &&
      !!currentProject && currentProject === project && currentProject.projectId === projectId;
  }

  function requireOperationLogSession(project, projectId) {
    if (operationLogSessionIsCurrent(project, projectId)) { return; }

    var error = new Error('operation log session changed');
    error.code = 'operation_log_session_changed';
    throw error;
  }

  function formatMessageLogForCopy(entries, project) {
    function cell(value) {
      return String(value == null ? '' : value).replace(/[\t\r\n]+/g, ' ');
    }

    var identity = operationLogIdentity(project);
    var lines = ['project_id\tproject_code\tdatabase_provider\toccurred_utc\tlevel\ttext'];
    (entries || []).slice(0, 100).reverse().forEach(function (entry) {
      lines.push([
        cell(identity.projectId),
        cell(identity.projectCode),
        cell(identity.databaseProvider),
        cell(entry.occurredUtc),
        cell(entry.level),
        cell(entry.text)
      ].join('\t'));
    });
    return lines.join('\r\n');
  }

  function writeClipboardText(value) {
    function fallback() {
      var textarea = global.document.createElement('textarea');
      textarea.value = value;
      textarea.setAttribute('readonly', '');
      textarea.style.position = 'fixed';
      textarea.style.left = '-9999px';
      textarea.style.opacity = '0';
      global.document.body.appendChild(textarea);
      textarea.focus();
      textarea.select();

      try {
        var copied = global.document.execCommand('copy');
        return copied
          ? Promise.resolve()
          : Promise.reject(new Error('clipboard copy was rejected'));
      } catch (error) {
        return Promise.reject(error);
      } finally {
        textarea.remove();
      }
    }

    if (global.navigator && global.navigator.clipboard && global.navigator.clipboard.writeText) {
      return global.navigator.clipboard.writeText(value).catch(fallback);
    }
    return fallback();
  }

  var messageCopyFeedbackTimer = null;

  function setMessageCopyFeedback(button, text) {
    global.clearTimeout(messageCopyFeedbackTimer);
    button.textContent = text;
    button.setAttribute('aria-label', text);
    messageCopyFeedbackTimer = global.setTimeout(function () {
      button.textContent = '複製紀錄';
      button.setAttribute('aria-label', '複製最近 100 則操作紀錄');
    }, 2200);
  }

  function copyRecentMessages(button) {
    var project = Store.getState().project;
    if (!project) {
      setMessageCopyFeedback(button, '尚未開啟案件');
      return Promise.resolve();
    }

    var projectId = project.projectId;
    button.disabled = true;
    button.textContent = '整理中…';
    button.setAttribute('aria-label', '正在整理最近 100 則操作紀錄');
    return Promise.resolve()
      .then(function () {
        requireOperationLogSession(project, projectId);
        return flushMessageWrites();
      })
      .then(function () {
        requireOperationLogSession(project, projectId);
        return global.JetApi.logRecent({ limit: 100 });
      })
      .then(function (data) {
        requireOperationLogSession(project, projectId);
        var messages = data && Array.isArray(data.messages) ? data.messages : [];
        return writeClipboardText(formatMessageLogForCopy(messages, project));
      })
      .then(function () {
        setMessageCopyFeedback(button, '已複製');
      })
      .catch(function (error) {
        setMessageCopyFeedback(
          button,
          error && error.code === 'operation_log_session_changed'
            ? '案件已切換，未複製'
            : '複製失敗');
      })
      .finally(function () {
        button.disabled = false;
      });
  }

  // 匯出進度只鏡射後端 export.progress 事件回報的實際里程碑；phase 不代表成功，也沒有可推算的總量或比例。
  // Payload 不完整、帶有未知欄位，或 phase／工作表語意不合法時安靜忽略，避免 busy chrome
  // 把不在公開契約內的資料呈現成權威狀態。
  function formatExportProgress(progress) {
    if (!progress || typeof progress !== 'object' || Array.isArray(progress)) { return null; }

    var contractFields = [
      'artifactKind',
      'phase',
      'sheetName',
      'sheetsCompleted',
      'rowsWritten',
      'elapsedMilliseconds'
    ];
    var payloadFields = Object.keys(progress);
    if (payloadFields.length !== contractFields.length || contractFields.some(function (field) {
      return !Object.prototype.hasOwnProperty.call(progress, field);
    }) || payloadFields.some(function (field) {
      return contractFields.indexOf(field) < 0;
    })) {
      return null;
    }

    var artifactLabels = {
      validationReport: '資料驗證報告',
      accountMapping: '科目配對檔',
      infReport: '資料可靠性抽樣清單',
      prescreenReport: '預篩選報告',
      criteriaSelectionReport: '條件篩選報告',
      workingPaper: '工作底稿'
    };
    var phaseLabels = {
      preparingData: '準備資料',
      writingSheet: '寫入工作表',
      finalizingWorkbook: '收尾活頁簿',
      publishingArtifact: '進入發布階段'
    };
    if (!Object.prototype.hasOwnProperty.call(artifactLabels, progress.artifactKind) ||
        !Object.prototype.hasOwnProperty.call(phaseLabels, progress.phase)) {
      return null;
    }

    var sheetsCompleted = progress.sheetsCompleted;
    var rowsWritten = progress.rowsWritten;
    var elapsedMilliseconds = progress.elapsedMilliseconds;
    if (!Number.isSafeInteger(sheetsCompleted) || sheetsCompleted < 0 ||
        !Number.isSafeInteger(rowsWritten) || rowsWritten < 0 ||
        !Number.isSafeInteger(elapsedMilliseconds) || elapsedMilliseconds < 0) {
      return null;
    }

    var isWritingSheet = progress.phase === 'writingSheet';
    var hasSheetName = typeof progress.sheetName === 'string' && progress.sheetName.trim().length > 0;
    if ((isWritingSheet && !hasSheetName) || (!isWritingSheet && progress.sheetName !== null) ||
        (progress.phase === 'preparingData' && (sheetsCompleted !== 0 || rowsWritten !== 0))) {
      return null;
    }

    var sheetText = isWritingSheet ? progress.sheetName : '—';
    return artifactLabels[progress.artifactKind] + '｜' + phaseLabels[progress.phase] +
      '｜工作表 ' + sheetText +
      '｜已關閉 ' + sheetsCompleted.toLocaleString('zh-Hant') + ' 張' +
      '｜已寫入 ' + rowsWritten.toLocaleString('zh-Hant') + ' 列' +
      '｜耗時 ' + elapsedMilliseconds.toLocaleString('zh-Hant') + ' 毫秒';
  }

  /* ---- 啟動 ---------------------------------------------------------------- */

  function init() {
    Store.subscribe(render);
    Store.subscribe(persistNewMessages);

    // JetApi action 常在 Ui.run factory 內接 .then()；由真正送出 request 的邊界通知，
    // 才不會因最外層 promise 已失去 requestId 而讓取消鈕消失。
    if (global.JetApi && typeof global.JetApi.onRequestStarted === 'function') {
      global.JetApi.onRequestStarted(function (request) {
        if (request && CANCELLABLE_ACTIONS[request.action] && Store.getState().busy) {
          busyActiveAction = request.action;
          Store.setBusyRequest(request.requestId);
        }
      });
    }

    // 長作業進度只更新 busy chrome；最終權威仍是 action response。
    if (global.JetApi && typeof global.JetApi.on === 'function') {
      global.JetApi.on('import.progress', function (progress) {
        if (!progress) { return; }
        Store.setBusyDetail('已讀取 ' + Number(progress.rowsRead || 0).toLocaleString() + ' 列');
      });
      global.JetApi.on('mapping.progress', function (progress) {
        if (!progress) { return; }
        var kind = progress.kind === 'tb' ? 'TB' : 'GL';
        var done = Number(progress.rowsProcessed || 0).toLocaleString();
        var total = Number(progress.totalRows || 0).toLocaleString();
        Store.setBusyDetail(kind + ' ' + done + ' / ' + total + ' 列');
      });
      global.JetApi.on('export.progress', function (progress) {
        var detail = formatExportProgress(progress);
        if (detail) { Store.setBusyDetail(detail); }
      });
    }

    var ready = !!(global.JetApi && global.JetApi.isReady && global.JetApi.isReady());
    Store.setBridgeReady(ready);

    var exitBtn = document.querySelector('[data-action="app-exit"]');
    if (exitBtn) {
      exitBtn.addEventListener('click', Ui.exitApp);
    }

    var backPickerBtn = document.querySelector('[data-action="app-back-picker"]');
    if (backPickerBtn) {
      backPickerBtn.addEventListener('click', Ui.backToPickerFromHeader);
    }

    var openProjectFolderBtn = document.querySelector('[data-action="open-project-folder"]');
    if (openProjectFolderBtn) {
      openProjectFolderBtn.addEventListener('click', function () {
        Ui.run('開啟案件資料夾', function () {
          return global.JetApi.hostOpenFolder({ target: 'projectFolder' });
        });
      });
    }

    var cancelOperationBtn = document.querySelector('[data-action="cancel-operation"]');
    if (cancelOperationBtn) {
      cancelOperationBtn.addEventListener('click', function () {
        var current = Store.getState();
        var targetRequestId = current.activeRequestId;
        if (!targetRequestId || current.cancellationRequested) { return; }

        Store.setCancellationRequested(true);
        global.JetApi.operationCancel({ requestId: targetRequestId })
          .then(function (result) {
            var latest = Store.getState();
            if (latest.busy && latest.activeRequestId === targetRequestId && result) {
              if (!result.requested) {
                Store.addMessage('目前動作已完成，已完成的結果保留；尚未開始的後續作業將停止。', 'info');
              } else {
                var snapshot = latest.busyDetail
                  ? '，當時進度：' + latest.busyDetail
                  : '';
                Store.addMessage(
                  '已要求取消「' + (latest.busyLabel || '目前作業') + '」' + snapshot + '。',
                  'info');
              }
            }
          })
          .catch(function (error) {
            var latest = Store.getState();
            if (latest.busy && latest.activeRequestId === targetRequestId) {
              Store.setCancellationRequested(false);
              Store.addMessage('取消作業失敗：' + error.message, 'warn');
            }
          });
      });
    }

    document.querySelectorAll('[data-action="messages-toggle"]').forEach(function (btn) {
      btn.addEventListener('click', Store.toggleMessagesPanel);
    });
    var copyMessagesButton = document.querySelector('[data-action="messages-copy"]');
    if (copyMessagesButton) {
      copyMessagesButton.addEventListener('click', function () {
        copyRecentMessages(copyMessagesButton);
      });
    }
    var supportLogButton = document.querySelector('[data-action="support-log-export"]');
    if (supportLogButton) {
      supportLogButton.addEventListener('click', function () {
        var project = Store.getState().project;
        if (!project) { return; }
        Ui.run('輸出支援日誌', function () {
          return global.JetApi.supportLogExport({ projectId: project.projectId }).then(function (data) {
            Store.addMessage('支援日誌已輸出：' + data.filePath, 'info');
          });
        });
      });
    }

    // 流程總覽（4b）：標題列鈕開啟；靜態背景遮罩關閉（卡片內動態 ✕ 於 bindOverview 綁定）。
    var overviewOpenBtn = document.querySelector('[data-action="overview-open"]');
    if (overviewOpenBtn) {
      overviewOpenBtn.addEventListener('click', function () {
        overviewReturnFocus = overviewOpenBtn;
        Store.setOverviewOpen(true);
      });
    }
    bindOverviewDialog();

    if (Ui.initDataPreview) { Ui.initDataPreview(); }
    if (Ui.initDevPanel) { Ui.initDevPanel(); }
    if (Ui.initDevLogPanel) { Ui.initDevLogPanel(); }
    Store.addMessage('畫面已載入。', 'info');

    if (!ready) {
      bridgeProbeSettled = true;
      Store.addMessage('應用程式連線尚未就緒；目前只能顯示基本畫面。', 'warn');
      render();
      return;
    }

    global.JetApi.systemPing({})
      .then(function (ping) {
        bridgeProbeSettled = true;
        Store.setBridgeReady(true);
        Store.setDevToolsEnabled(ping && ping.devToolsEnabled === true);
        // 背景查 SQL Server 後端身分，完成後在訊息面板補一則（連到哪台／版本／是否 Express）。
        // 不阻塞專案載入——伺服器慢／不可達時 connect timeout 可能達數秒。
        global.JetApi.systemDatabaseInfo({}).then(function (info) {
          var s = info && info.sqlServer;
          // 建立案件的 SQL Server 選項只在確定沒有設定時停用；查詢失敗或還沒回來時維持可選。
          if (s && typeof s.configured === 'boolean') { Store.setSqlServerConfigured(s.configured); }
          if (s && s.summary) {
            Store.addMessage(s.summary, (s.configured && !s.reachable) ? 'warn' : 'info');
            // 容量預警（純呈現）：整庫大小超過前端門檻時補一則 warn。schemaCount＝線上專案數（＝prj_ schema 數）。
            if (s.reachable && typeof s.databaseSizeMb === 'number' && s.databaseSizeMb > DB_CAPACITY_WARN_MB) {
              Store.addMessage(
                '資料庫容量偏大：單庫 ' + s.databaseSizeMb + ' MB（' + (s.schemaCount || 0) +
                ' 個線上專案），已超過 ' + DB_CAPACITY_WARN_MB + ' MB 預警門檻；請系統管理人員留意容量與備份／清理規劃。',
                'warn');
            }
            render();
          }
        }, function () { /* 身分查詢失敗不阻斷啟動，靜默略過 */ });
        // 與 databaseInfo 並行取得使用者身分（右上角身分徽章與 picker 註記用）。
        // 純 UI 狀態；失敗（bridge 錯誤）靜默略過，不阻斷啟動、不進訊息面板。
        global.JetApi.systemWhoAmI({}).then(function (who) {
          if (who) { Store.setCurrentUser(who); }
        }, function () { /* whoAmI 失敗靜默略過 */ });
        return Ui.loadProjects();
      })
      .catch(function (error) {
        bridgeProbeSettled = true;
        Store.setBridgeReady(false);
        Store.addMessage('應用程式連線失敗：' + error.message, 'warn');
      })
      .finally(render);

    render();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})(window);
