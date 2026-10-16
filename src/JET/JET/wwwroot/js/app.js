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
    'filter.scenario.save': true,
    'filter.scenario.remove': true,
    'filter.scenario.recheck': true,
    'export.validationArtifacts': true,
    'export.accountMappingTemplate': true,
    'export.prescreenReport': true,
    'export.criteriaSelectionReport': true,
    'export.workpaperStream': true
  };

  // 增量渲染狀態
  var lastNavKey = null;
  var lastContentKey = null;
  var contentComposition = null;
  var lastStepScrollKey = null; // 上次已捲頂的 (view, step)；步驟／視圖切換時把中欄捲回頂部
  var stepFoldingAway = [];     // 換步驟時正在收回各自那一列的舊內容（見 renderContent）
  var lastPickerKey = null;
  var pickerShownBefore = false; // 上一次畫面是不是案件清單；從流程畫面切回來時不播加入與移除的動態
  var lastMessageId = 0;
  var messagesEmptyShown = false;
  // 開機 bridge 探測是否已有結論（systemPing 成敗任一終態、或無 host 時即 true）。
  // 讓身分徽章區分「偵測中」（尚未結論）與「主機連線失敗」（已結論且未就緒）；純渲染旗標、不進 state、不進後端 payload。
  var bridgeProbeSettled = false;

  // 本階段共用的最小焦點 seam：app 殼層持有移動焦點與 dialog Tab 圈限，
  // 步驟模組只透過 JetFocus 使用「安全移動／延後移動」，不把此純 UI 行為擴張到 wire。
  // options.preventScroll：畫面正由動態平順捲到這個元素時（例如加入的條件），移焦點不要再瞬間捲一次。
  function focusElement(element, options) {
    if (!element || typeof element.focus !== 'function' || !element.isConnected) { return false; }
    if (options && options.preventScroll) { element.focus({ preventScroll: true }); } else { element.focus(); }
    return document.activeElement === element;
  }

  function deferFocus(resolveElement, options) {
    global.setTimeout(function () {
      var element = typeof resolveElement === 'function' ? resolveElement() : resolveElement;
      focusElement(element, options);
    }, 0);
  }

  // 按下的控制項跟著重繪不見或變成不能按時，焦點交給接替它的控制項（使用者 2026-10-13 交接）：
  // 依序找畫面上第一個能拿焦點的，都沒有就不動。收起中的舊元素是重繪前的複本，不算。
  function deferFocusFirst(selectors) {
    deferFocus(function () {
      for (var i = 0; i < selectors.length; i++) {
        if (!selectors[i]) { continue; }
        var found = Array.prototype.find.call(document.querySelectorAll(selectors[i]), function (element) {
          return !element.closest('[data-motion-leaving], [inert]') && !element.disabled && element.getClientRects().length > 0;
        });
        if (found) { return found; }
      }
      return null;
    });
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
    deferFirst: deferFocusFirst,
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
      if (!busyWasBusy) {
        startBusyElapsedClock();
        // inert 會讓焦點掉到整個頁面；先記下來，作業結束時放回去。
        busyFocusMemory = { focus: captureContentFocus(mainEl), selection: captureContentTextSelection(mainEl) };
      }
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
          ? '資料量大時需要較長時間，可按「取消作業」中止。'
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
      if (busyWasBusy) { restoreBusyFocus(mainEl, overlayEl); }
    }
    busyWasBusy = state.busy;
  }

  // 作業進行中 <main> 是 inert，焦點會掉到整個頁面，按 Tab 就從頭開始（使用者 2026-10-13 交接）。
  // 作業結束時把焦點放回開始前的控制項；重繪換掉了它，就用和中欄重繪同一套認法找回來。
  // 找不到就不動，交給操作自己用 JetFocus.defer 指定的接替者；焦點已經在別處（例如作業開了對話框）也不動。
  var busyFocusMemory = null;

  function restoreBusyFocus(mainEl, overlayEl) {
    var memory = busyFocusMemory;
    busyFocusMemory = null;
    var active = document.activeElement;
    if (!memory || !memory.focus || (active && active !== document.body && !overlayEl.contains(active))) { return; }
    var element = findContentFocus(mainEl, memory.focus);
    if (focusElement(element, { preventScroll: true })) { restoreContentTextSelection(element, memory.selection); }
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
      renderSummary(state);
      renderPicker(state);
      renderMessages(state);
      renderOverview(state);
      return;
    }

    if (picker) { picker.hidden = true; }
    pickerShownBefore = false;
    if (body) { body.hidden = false; }
    // 右欄常駐；收合為純 UI 狀態，切 class 不影響資料載入節制。
    if (dataPreview) { dataPreview.classList.toggle('data-preview--collapsed', !!state.dataPreviewCollapsed); }
    // 開發面板只在 Debug 組建顯示（system.ping.devToolsEnabled；Release 連 action 都未註冊）
    if (devPanel) { devPanel.hidden = !state.devToolsEnabled; }
    if (devLogPanel) { devLogPanel.hidden = !state.devToolsEnabled; }
    if (backPicker) { backPicker.hidden = false; }
    if (overviewBtn) { overviewBtn.hidden = false; }
    if (openProjectFolder) { openProjectFolder.hidden = false; }

    renderSummary(state);
    renderStepNav(state, steps);
    renderContent(state, current);
    renderMessages(state);
    renderOverview(state);
  }

  function renderSummary(state) {
    renderIdentityBadge(state);
    Ui.setText('case-id', state.caseId || '尚未建立');
    Ui.setText('case-client', state.caseClient || '—');
    Ui.setText('case-period', state.view === 'picker' ? '—' : (auditPeriodText(state) || '—'));
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
      '<div class="project-row" data-project-id="' + Ui.esc(p.projectId) + '" data-motion-key="' + Ui.esc('project-' + p.projectId) + '">' +
        '<button type="button" class="project-row__open" data-action="picker-open" data-project-id="' +
          Ui.esc(p.projectId) + '" data-project-provider="' + Ui.esc(p.databaseProvider) + '" ' +
          'aria-label="開啟案件 ' + Ui.esc(displayName) + '">' +
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
          'title="刪除案件" aria-label="刪除案件">' + ICON_TRASH + '</button>' +
      '</div>'
    );
  }

  function pickerFeedbackHtml(message, context, level) {
    if (!message) { return ''; }
    // 操作已成功、只是留下要審計員自己處理的事時，用琥珀色提示，不寫「無法完成操作」（使用者 2026-10-13 裁定訊息分三級）。
    if (level === 'warning') {
      return '<p class="form-notice form-notice--warning picker-feedback--warning" data-motion-key="picker-feedback" role="status">' +
        Ui.esc(message) + '</p>';
    }
    // 錯誤框只寫發生什麼事與能做什麼，不放按鈕（使用者 2026-10-13 選「從錯誤框拿掉，改成一般工具」）。
    return '<div class="picker-feedback" data-motion-key="picker-feedback" role="alert" aria-live="polite">' +
      '<strong class="picker-feedback__title">無法完成操作</strong>' +
      '<span class="picker-feedback__body">' + Ui.esc(message) + '</span>' +
      '</div>';
  }

  // 案件清單看不到訊息面板，所以「輸出支援日誌」放在下方操作列最後，是給維護 JET 的人用的一般工具，畫面不叫審計員按。
  // 不帶 projectId：後端匯出全部去識別紀錄到 logs 資料夾。Debug 組建剛發生開案或刪除失敗時，
  // 原始診斷日誌的出口也放在同一列，並帶同一次 correlation（system.ping.devToolsEnabled）。
  function pickerToolsHtml(state) {
    var context = state.pickerFeedbackContext;
    var devExport = state.devToolsEnabled && context && context.projectId
      ? '<button type="button" class="btn btn--ghost" data-action="picker-dev-log-export">輸出診斷日誌（DEV）</button>'
      : '';
    var results = [];
    if (state.pickerSupportLogPath) { results.push('支援日誌已輸出：' + state.pickerSupportLogPath); }
    if (context && context.devExportedPath) { results.push('診斷日誌已輸出：' + context.devExportedPath); }
    return '<button type="button" class="btn btn--ghost picker-panel__tool" data-action="picker-support-export">輸出支援日誌</button>' +
      devExport +
      (results.length
        ? '<span class="picker-panel__tool-result" role="status">' + Ui.esc(results.join('；')) + '</span>'
        : '');
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
      state.pickerFeedbackLevel + '|' + state.devToolsEnabled + '|' + (state.pickerSupportLogPath || '') + '|' +
      (feedbackContext.projectId || '') + '|' + (feedbackContext.correlationId || '') + '|' +
      (feedbackContext.devExportedPath || '');
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

    // 失聯降級：不擋操作的「需要注意」提示（使用者 2026-10-09 裁定琥珀色）；此時線上區列的是本機快取條目。
    // 後端 message 與這句同義，不再接在後面重複一次。
    var onlineAlert = unreachable
      ? '<div class="form-notice form-notice--warning picker-section__alert" data-motion-key="picker-online-alert" role="status">' +
          (onlineRows.length ? '目前連不上線上案件，下面先列出這台電腦上次記下的清單。' : '目前連不上線上案件。') + '</div>'
      : '';

    // 未同步、同步後確實無案件、同步失聯是三個不同狀態；失聯以警示條代替空狀態句。
    var onlineBody = onlineRows.length
      ? onlineRows.join('')
      : (onlineNotSynced
          ? '<p class="picker-section__empty" data-motion-key="online-not-synced">尚未同步線上案件。</p>'
          : (unreachable ? '' : '<p class="picker-section__empty" data-motion-key="online-empty">目前沒有你可存取的線上案件。</p>'));
    var localBody = localRows.length
      ? localRows.join('')
      : '<p class="picker-section__empty" data-motion-key="local-empty">尚無本地案件。</p>';

    // 加入與移除的動態（JetMotion）：案件清單已經在畫面上時，新增的案件長出來、刪除的收起來；從流程畫面切回來時是整頁換掉，不比對。
    var motion = contentMotion(container, pickerShownBefore);
    container.innerHTML =
      '<div class="picker-panel">' +
        '<h2 class="picker-panel__title">選擇案件</h2>' +
        '<p class="picker-panel__hint">選擇案件繼續作業，或建立新案件。</p>' +
        pickerFeedbackHtml(state.pickerFeedback, state.pickerFeedbackContext, state.pickerFeedbackLevel) +
        '<section class="picker-section">' +
          '<div class="picker-section__head">' +
            '<h3 class="picker-section__title">線上資料庫（SQL Server）</h3>' +
            principalNote +
          '</div>' +
          onlineAlert +
          '<div class="picker-section__list" data-motion-list="online">' + onlineBody + '</div>' +
        '</section>' +
        '<section class="picker-section">' +
          '<div class="picker-section__head">' +
            '<h3 class="picker-section__title">本地資料庫</h3>' +
          '</div>' +
          '<div class="picker-section__list" data-motion-list="local">' + localBody + '</div>' +
        '</section>' +
        '<div class="picker-panel__list">' +
          '<button type="button" class="project-row project-row--new" data-action="picker-new">' +
            '<span class="project-row__plus">' + ICON_PLUS + '</span>' +
            '<span class="project-row__main">' +
              '<span class="project-row__title">新增案件</span>' +
            '</span>' +
          '</button>' +
        '</div>' +
        '<div class="picker-panel__actions">' +
          '<button type="button" class="btn btn--ghost" data-action="picker-refresh-local">重新整理本機</button>' +
          '<button type="button" class="btn" data-action="picker-sync-online">同步線上案件</button>' +
          pickerToolsHtml(state) +
        '</div>' +
      '</div>' +
      '<div class="modal" data-bind="delete-modal" hidden>' +
        '<div class="modal__backdrop" data-action="modal-cancel"></div>' +
        '<div class="modal__card" role="dialog" aria-modal="true" aria-labelledby="delete-modal-title">' +
          '<h3 class="modal__title" id="delete-modal-title">刪除案件</h3>' +
          '<p class="modal__body">確定要刪除「<span data-bind="delete-modal-name"></span>」嗎？' +
            '<span data-bind="delete-modal-detail"></span></p>' +
          '<button type="button" class="btn btn--ghost" data-action="delete-preview-retry" data-collapsible hidden>重新確認數量</button>' +
          '<div class="modal__actions">' +
            '<button type="button" class="btn btn--ghost" data-action="modal-cancel">取消</button>' +
            '<button type="button" class="btn btn--danger" data-action="modal-confirm">刪除</button>' +
          '</div>' +
        '</div>' +
      '</div>';

    bindPicker(container);
    motion.play();
    motion.finish();
    pickerShownBefore = true;
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

    // 一般工具：不帶 projectId，後端匯出全部去識別紀錄；結果寫在按鈕旁，案件清單上原本的提示不動。
    var exportSupport = container.querySelector('[data-action="picker-support-export"]');
    if (exportSupport) {
      exportSupport.addEventListener('click', function () {
        Ui.run('輸出支援日誌', function () {
          return global.JetApi.supportLogExport({}).then(function (data) {
            Store.setPickerSupportLogPath(data.filePath);
          });
        }, {
          // 失敗時原因寫在案件清單的錯誤框；保留原本的開案失敗資訊，DEV 按鈕仍在。
          inlineError: true,
          onError: function (message) { Store.setPickerFeedback(message, Store.getState().pickerFeedbackContext); }
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
          inlineError: true,
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
        // 刪除後重新讀回案件清單時，這一列先輕微快速地晃一下再收起（使用者 2026-10-13 要求刪除案件時也這樣）。
        if (global.JetMotion) { global.JetMotion.removing({ keys: 'project-' + target.id, wait: 20000 }); }
        closeModal(false);
        Store.setPickerFeedback(null);
        var cleanupWarning = null;
        Ui.run('刪除案件', function () {
          return global.JetApi.projectDelete({ projectId: target.id }).then(function (data) {
            Store.addMessage('已刪除案件「' + target.name + '」。', 'info');
            // 本機資料夾清理失敗時後端仍回 ok，附 message（DB 刪除已成功）——照實提示，不當作刪除失敗。
            // 案件清單看不到訊息面板，所以同一句也寫在清單上方（琥珀色）；清單重新讀回時會先清掉提示，所以讀回後才寫。
            if (data && data.message) {
              Store.addMessage(data.message, 'warn');
              cleanupWarning = data.message;
            }
          });
        }, {
          refresh: function () {
            return Ui.loadProjects().then(function () {
              // 重新讀回清單失敗時，畫面上已經是那個錯誤；資料夾的提醒仍留在訊息面板。
              if (cleanupWarning && !Store.getState().pickerFeedback) {
                Store.setPickerFeedback(cleanupWarning, null, 'warning');
              }
            });
          },
          refreshWhen: 'success',
          // 刪除後重新讀取清單失敗時的操作名稱（使用者 2026-10-13 勾選頁確認 20）。
          refreshLabel: '重新讀取案件清單',
          inlineError: true,
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

  // 換步驟在按下時就開始，和 macOS 側邊欄一樣：原本要等放開滑鼠才換，按住的那一下畫面不動
  // （使用者 2026-10-15：「點下去的時候我會預期元素開始移動，但他會頓一下才開始移動」）。
  // 只認滑鼠主要按鍵、沒有按著修飾鍵的按下；按下後的那一次放開（click）不再換一次。觸控與觸控筆按下時可能是要捲動，
  // 和鍵盤的 Enter、空白鍵一樣照舊走 click。
  // 按下就換會讓兩件原本由放開前的失焦處理的事落空，所以先做：正在編輯的欄位先失焦，只在 change 存值的欄位
  // （例如第五步「期末前 X 天」）才不會隨中欄重建而遺失；擋下按下的預設動作，瀏覽器才不會在換步驟開出的
  // 確認對話框之後，再把焦點移回這顆按鈕（2026-10-16 提交前複審找到）。
  function bindStepPress(element, index) {
    var pressed = false;
    element.addEventListener('pointerdown', function (event) {
      if (event.pointerType !== 'mouse' || event.button !== 0 || event.isPrimary === false
        || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) { return; }
      var active = document.activeElement;
      if (active && active !== element && active !== document.body && typeof active.blur === 'function') { active.blur(); }
      event.preventDefault();
      pressed = true;
      navigateToStep(index);
    });
    element.addEventListener('click', function (event) {
      var released = pressed && event.detail > 0;
      pressed = false;
      if (!released) { navigateToStep(index); }
    });
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
          '<span class="toc-item__label"' + (status === 'current' ? ' data-bind="case-step"' : '') + '>' + Ui.esc(step.label) + '</span>' +
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
      bindStepPress(btn, parseInt(btn.getAttribute('data-step-index'), 10));
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
      if (imp.gl) { seg.push('PBC-GL ' + fmtNum(imp.gl.rowCount) + ' 筆'); }
      if (imp.tb) { seg.push('PBC-TB ' + fmtNum(imp.tb.rowCount) + ' 筆'); }
      return seg.length ? seg.join(' / ') : '匯入總帳明細與試算表';
    }

    if (id === 'mapping') {
      return ['gl', 'tb'].map(function (kind) {
        var label = kind.toUpperCase();
        if (state.mapping[kind].committed) { return label + ' 已確認配對'; }
        if (!imp[kind]) { return 'PBC-' + label + ' 尚未匯入'; }
        return label + ' 待確認配對';
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

  // 審計員自己按的操作失敗或被阻擋、那個操作又沒有就地錯誤框時，在目前步驟標題下方寫出動作與原因
  // （使用者 2026-10-13 裁定）。失敗是紅色；被阻擋是琥珀色，因為那是前置條件還沒完成，不是執行失敗。
  // 由 Ui.run 寫入 state.operationFailure；重試任何操作或換步驟時拿掉。
  function operationFailureHtml(state, index) {
    var failure = state.operationFailure;
    if (!failure || failure.stepIndex !== index || !failure.message) { return ''; }
    return '<p class="form-notice' + (failure.level === 'warn' ? ' form-notice--warning' : '') +
      ' stepflow-item__failure" role="alert" data-motion-key="operation-failure">' + Ui.esc(failure.message) + '</p>';
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
          operationFailureHtml(state, index) +
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
      bindStepPress(el, index);
      el.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); navigateToStep(index); }
      });
    });

    container.querySelectorAll('[data-step-toggle]').forEach(function (el) {
      // 收合時整段內容先收回標題，收完才換成一行摘要；展開時由 renderContent 讓內容從標題長出來。
      var toggle = function () {
        var body = Store.getState().stepFlowCollapsed ? null : container.querySelector('.stepflow-item--current .stepflow-item__body');
        if (body && global.JetMotion) {
          global.JetMotion.fold(body, function () { Store.setStepFlowCollapsed(true); });
          return;
        }
        Store.setStepFlowCollapsed(!Store.getState().stepFlowCollapsed);
      };
      el.addEventListener('click', toggle);
      el.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); }
      });
    });
  }

  function focusCurrentStepHeading(container, state, announce, preventScroll) {
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
    }, preventScroll ? { preventScroll: true } : null);
  }

  // 換步驟時中欄捲回頂部的動態：和原本那一步收起來用同一條彈簧（SPRINGS.close），新步驟的標題一路朝同一個方向移到定位。
  // 原本第一格就一下跳到頂部、移焦點時又瞬間捲到標題，接著才開始收放，看起來像頓了一下才動（使用者 2026-10-15）。
  // 捲動期間關掉瀏覽器的捲動錨定，上方收起的內容才不會讓瀏覽器另外改捲軸；使用者自己捲了就放手。不能播動態時直接回到頂部。
  var contentGlide = null;

  function stopContentGlide() {
    if (!contentGlide) { return; }
    var glide = contentGlide;
    contentGlide = null;
    glide.stop();
  }

  function glideContentHome(container, from) {
    stopContentGlide();
    if (!motionAllowed() || typeof global.requestAnimationFrame !== 'function') {
      container.scrollTop = 0;
      return;
    }
    container.scrollTop = from;
    var origin = container.scrollTop;
    if (origin < 1) {
      container.scrollTop = 0;
      return;
    }
    var start = motionNow();
    var settle = springSettle(SPRINGS.close, -1, 0);
    var last = origin;
    var frame = 0;
    var glide = {
      stop: function () {
        if (frame && typeof global.cancelAnimationFrame === 'function') { global.cancelAnimationFrame(frame); }
        frame = 0;
        container.style.overflowAnchor = '';
      }
    };
    function tick() {
      frame = 0;
      if (contentGlide !== glide) { return; }
      // 內容變短時瀏覽器會把捲軸夾到新的底部，方向和捲回頂部相同，不算使用者自己捲。
      var clamped = container.scrollTop < last && container.scrollTop >= container.scrollHeight - container.clientHeight - 1;
      if (!container.isConnected || (Math.abs(container.scrollTop - last) > 2 && !clamped)) {
        stopContentGlide();
        return;
      }
      var elapsed = motionNow() - start;
      var progress = elapsed >= settle ? 1 : Math.min(springProgress(SPRINGS.close, elapsed), 1);
      container.scrollTop = Math.max(origin * (1 - progress), 0);
      last = container.scrollTop;
      if (progress >= 1) {
        stopContentGlide();
        return;
      }
      frame = global.requestAnimationFrame(tick);
    }
    container.style.overflowAnchor = 'none';
    contentGlide = glide;
    frame = global.requestAnimationFrame(tick);
  }

  // 「凡寫入即 bump 重繪」慣例（state.js）的框架層配套：重繪後把焦點放回同一個控制項。
  // 標了 data-focus-key 的照舊用它找。沒標的用控制項本身認得出來的地方找：所在區塊的 data-motion-key、
  // 標籤、type、role、aria-label、name、id 與 data- 屬性；這樣仍有兩個以上一樣時，再加上按鈕上的字（數字不算，筆數會變）。
  // 重繪前後都只有一個符合時才放回去，有兩個以上就不猜，避免把焦點送到同名的另一個控制項。
  // 原本只認 data-focus-key，沒標的按鈕與勾選框重繪後焦點都落到整個頁面（使用者 2026-10-13 交接）。
  var FOCUS_CANDIDATES = 'button, summary, a[href], input:not([type="hidden"]), select, textarea, [tabindex]';
  var FOCUS_TRANSIENT = /^data-(?:focus-key|motion-(?:leaving|rendering|revealing|folding))$/;

  function focusSignature(element, withText) {
    var holder = element.parentElement && element.parentElement.closest('[data-motion-key]');
    var parts = [holder ? holder.getAttribute('data-motion-key') : '', element.tagName,
      element.getAttribute('type') || '', element.getAttribute('role') || '', element.getAttribute('aria-label') || '',
      element.getAttribute('name') || '', element.id || ''];
    Array.prototype.forEach.call(element.attributes || [], function (attribute) {
      if (attribute.name.indexOf('data-') === 0 && !FOCUS_TRANSIENT.test(attribute.name)) {
        parts.push(attribute.name + '=' + attribute.value);
      }
    });
    if (withText && /^(?:BUTTON|SUMMARY|A)$/.test(element.tagName)) {
      parts.push((element.textContent || '').replace(/\d[\d,.]*/g, '#').replace(/\s+/g, ' ').trim());
    }
    return parts.join('\u0001');
  }

  // 收起中的舊元素是重繪前的複本，不能拿焦點，也不算在一樣的控制項裡。
  function focusCandidates(container) {
    return Array.prototype.filter.call(container.querySelectorAll(FOCUS_CANDIDATES), function (element) {
      return !element.closest('[data-motion-leaving], [inert]');
    });
  }

  function captureContentFocus(container) {
    var active = document.activeElement;
    if (!active || active === container || !container.contains(active)) { return null; }
    var key = active.getAttribute('data-focus-key');
    if (key) { return { selector: '[data-focus-key="' + global.CSS.escape(key) + '"]' }; }
    var candidates = focusCandidates(container);
    for (var level = 0; level < 2; level++) {
      var signature = focusSignature(active, level > 0);
      var same = candidates.filter(function (element) { return focusSignature(element, level > 0) === signature; });
      if (same.length === 1) { return { signature: signature, withText: level > 0 }; }
    }
    return null;
  }

  function findContentFocus(container, memory) {
    if (!memory) { return null; }
    if (memory.selector) { return container.querySelector(memory.selector); }
    var same = focusCandidates(container).filter(function (element) {
      return focusSignature(element, memory.withText) === memory.signature;
    });
    return same.length === 1 ? same[0] : null;
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
    var failure = state.operationFailure;
    var key = state.view + '|' + current.id + '|' + state.contentVersion + '|' +
      (failure ? failure.id : '') + '|' + (state.stepFlowCollapsed ? 'c' : 'e');
    if (key === lastContentKey) { return; }
    // 只切換目前步驟的收合與展開：整段內容當成一塊，展開時從標題長出來（收合時 bindStepFlow 已經先收回標題），不逐項比對。
    var stepFolded = !!lastContentKey && key.slice(0, -1) === lastContentKey.slice(0, -1);
    lastContentKey = key;

    // 當步驟內的背景預覽等純資料更新重建 content 時，若焦點正在當前標題，
    // 重建後回到新節點；不重複發送「已切步」宣告。
    var activeElement = document.activeElement;
    var preserveStepHeadingFocus = !!activeElement && activeElement.matches(
      '[data-bind="current-step-title"][data-step-index="' + state.currentStepIndex + '"]');
    var focusMemory = captureContentFocus(container);
    var textSelection = focusMemory ? captureContentTextSelection(container) : null;
    var scrollPositions = captureContentScrollPositions(container);
    var contentScrollTop = container.scrollTop;
    var contentScrollLeft = container.scrollLeft;

    // 進入新步驟時把中欄捲回頂部：.content 是垂直捲動容器（overflow-y:auto），重建 innerHTML
    // 不會重置它的 scrollTop——上一步在頁尾按「下一步」殘留的捲動位置會讓長步驟（如資料驗證）
    // 一進來就停在頁尾。只在 (view, step) 真的改變時觸發；同一步驟內的資料更新／收合重繪不動捲軸，
    // 也不干涉步驟內既有的橫向捲動保留（mapping 對照表格）。
    var stepScrollKey = state.view + '|' + current.id;
    var stepChanged = stepScrollKey !== lastStepScrollKey;

    // 換步驟像同一排的展開區（使用者 2026-10-13 要求展開與收合一致：完成的步驟列和目前步驟一樣有展開箭頭，
    // 原本按下去卻是一瞬間換掉）：原本那一步的內容收回它的那一列，新的一步從標題長出來。
    // 只在流程畫面裡換步驟時這樣做；從案件清單開啟案件時直接顯示。
    // 長到一半或收到一半又整段重繪時（例如換步驟後讀回的資料），從此刻的進度接著播。
    var oldBody = global.JetMotion ? container.querySelector('.stepflow-item--current > .stepflow-item__body') : null;
    var oldHead = oldBody ? container.querySelector('.stepflow-item--current > .stepflow-item__head') : null;
    var switching = stepChanged && lastStepScrollKey !== null;
    var foldingBody = switching && oldHead && oldBody.getClientRects().length
      ? { element: oldBody, index: oldHead.getAttribute('data-step-index'), from: global.JetMotion.progress(oldBody) } : null;
    var bodyGrowth = !stepChanged && oldBody ? global.JetMotion.progress(oldBody) : null;
    if (stepChanged && !switching) { stepFoldingAway = []; }

    // 加入與移除的動態（JetMotion）：同一步裡的重繪先記下畫面上的東西，重繪後讓新出現的長出來、消失的收起來；換步驟是整塊換掉，不逐項比對。
    var motion = contentMotion(container, !stepChanged && !stepFolded);
    try {
      var html = '<div class="stepflow">';
      Store.STEPS.forEach(function (step, index) {
        html += stepSectionHtml(state, step, index);
      });
      html += '</div>';
      container.innerHTML = html;

      if (stepChanged) {
        lastStepScrollKey = stepScrollKey;
        // 在流程裡換步驟時，捲回頂部交給下面的 glideContentHome，第一格先停在原位。
        if (!switching) {
          stopContentGlide();
          container.scrollTop = 0;
        }
      }

      // 展開的當前步驟：把 body 元素交給既有已註冊渲染器（等同過去傳 content 容器）。
      if (!state.stepFlowCollapsed) {
        var body = container.querySelector('.stepflow-item--current .stepflow-item__body');
        if (body) { Ui.renderStep(current.id, body, state); }
      }
      // 統計數字的字級在比對加入與移除之前排好，動態量到的就是最後的高度。
      fitStatRows(container);

      // 先放好要收起來的舊東西、讓新東西從 0 開始，再還原捲軸：還原時的版面和重繪前一樣，捲軸不會跳。
      motion.play();
      var newBody = container.querySelector('.stepflow-item--current > .stepflow-item__body');
      if (global.JetMotion) {
        // 原本那一步的內容放進它現在的那一列，收回去再拿掉；整段重繪時重新放回同一列，接著收。
        var stepRow = function (index) { return container.querySelector('.stepflow > .stepflow-item[data-step-index="' + index + '"]'); };
        stepFoldingAway = stepFoldingAway.filter(function (entry) {
          var row = global.JetMotion.progress(entry.element) ? stepRow(entry.index) : null;
          if (row) { row.appendChild(entry.element); }
          return !!row;
        });
        if (foldingBody && stepRow(foldingBody.index) &&
            global.JetMotion.leave(foldingBody.element, stepRow(foldingBody.index), foldingBody.from)) {
          stepFoldingAway.push(foldingBody);
        }
      }
      if (newBody && !state.stepFlowCollapsed && global.JetMotion) {
        if (bodyGrowth && bodyGrowth.to === 1) {
          global.JetMotion.enter(newBody, bodyGrowth);
        } else if ((stepFolded || switching) && !bodyGrowth) {
          global.JetMotion.enter(newBody);
        }
      }
      // 原本那一步的內容放回去之後，中欄才回到原本的捲動位置，從那裡平順捲回頂部。
      if (switching) { glideContentHome(container, contentScrollTop); }

      // 同一步驟內的資料更新重繪：還原標記容器的捲動與輸入焦點；跨步切換不還原
      //（新步驟由 focusCurrentStepHeading 設定起始焦點，捲動另由上方捲頂邏輯處理）。
      if (!stepChanged) {
        restoreContentScrollPositions(container, scrollPositions);
        if (focusMemory && !preserveStepHeadingFocus) {
          var focused = findContentFocus(container, focusMemory);
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
        // 換步驟時標題由收放與捲動帶到定位，移焦點不另外瞬間捲過去；捲回頂部途中同一步重繪也一樣。
        focusCurrentStepHeading(container, state, stepChanged, switching || contentGlide !== null);
      }
      revealOperationFailure(container, failure);
    } finally {
      motion.finish();
    }
  }

  // 步驟失敗提示第一次畫出來時，不在看得到的範圍就平順捲過去；減少動態時直接跳。焦點不動。
  var revealedOperationFailureId = null;
  function revealOperationFailure(container, failure) {
    if (!failure || failure.id === revealedOperationFailureId) { return; }
    var notice = container.querySelector('.stepflow-item__failure');
    if (!notice) { return; }
    revealedOperationFailureId = failure.id;
    if (typeof notice.scrollIntoView === 'function') {
      notice.scrollIntoView({ block: 'nearest', behavior: motionAllowed() ? 'smooth' : 'auto' });
    }
  }

  // 加入與移除的動態（JetMotion）只管呈現；它不在時（例如只載入這段畫面程式的測試替身）重繪照常進行。
  // 重繪期間在容器加 data-motion-rendering，讓原地展開區不要把重建當成展開而重播（見 app.css 的 [data-collapsible]）。
  function contentMotion(container, allowed) {
    var engine = global.JetMotion;
    if (!engine || typeof container.setAttribute !== 'function') {
      return { play: function () {}, finish: function () {} };
    }
    var snapshot = engine.begin(container, allowed);
    container.setAttribute('data-motion-rendering', '');
    return {
      play: function () { engine.play(container, snapshot); },
      finish: function () {
        void container.offsetHeight;
        container.removeAttribute('data-motion-rendering');
      }
    };
  }

  // 統計數字不從中間斷開（使用者 2026-10-13 交接：總覽的十億級金額斷成兩行，審計員容易看錯）：
  // 數字放不下時整列一起縮一階，28、22、18px 都放不下才改成較少的格數，再從 28px 試起。
  // 同一列的數字一樣大，借方與貸方才比得起來。只換 app.css 的 stat-fit--* class，不改 state。
  var STAT_ROWS = '.overview-pop__grid, .stats-bar';
  var STAT_VALUES = '.overview-pop__kpi-value, .stat-card__value';
  var STAT_LAYOUTS = ['', 'stat-fit--stacked', 'stat-fit--single'];
  var STAT_SIZES = ['', 'stat-fit--xl', 'stat-fit--lg'];

  function fitStatRows(root) {
    if (!root || typeof root.querySelectorAll !== 'function') { return; }
    Array.prototype.forEach.call(root.querySelectorAll(STAT_ROWS), fitStatRow);
  }

  function fitStatRow(row) {
    if (row.closest('[data-motion-leaving]')) { return; }
    var values = row.querySelectorAll(STAT_VALUES);
    if (!values.length) { return; }
    var columns = Infinity;
    var smallest = null;
    for (var i = 0; i < STAT_LAYOUTS.length; i++) {
      setStatFit(row, STAT_LAYOUTS[i], '');
      // 窄版的媒體查詢已經減少格數時，不會更少的版面就跳過。
      var count = statColumnCount(row);
      if (count >= columns) { continue; }
      columns = count;
      for (var j = 0; j < STAT_SIZES.length; j++) {
        setStatFit(row, STAT_LAYOUTS[i], STAT_SIZES[j]);
        if (statValuesFit(values)) { return; }
        smallest = [STAT_LAYOUTS[i], STAT_SIZES[j]];
      }
    }
    // 窄到怎麼排都放不下時停在最小的字級與最少的格數，數字仍不斷開。
    if (smallest) { setStatFit(row, smallest[0], smallest[1]); }
  }

  function setStatFit(row, layout, size) {
    STAT_LAYOUTS.concat(STAT_SIZES).forEach(function (name) {
      if (name) { row.classList.toggle(name, name === layout || name === size); }
    });
  }

  function statColumnCount(row) {
    var tracks = global.getComputedStyle(row).gridTemplateColumns;
    return tracks && tracks !== 'none' ? tracks.trim().split(/\s+/).length : 1;
  }

  function statValuesFit(values) {
    for (var i = 0; i < values.length; i++) {
      if (values[i].scrollWidth > values[i].clientWidth) { return false; }
    }
    return true;
  }

  // 重繪時由 renderContent 與 renderOverviewCard 直接排好；這裡處理之後中欄或視窗寬度改變，以及字型晚到。
  // 量的是中欄與總覽的全視窗外層，它們的大小不隨統計數字改變，排版時不會再觸發自己。
  function bindStatFit() {
    var targets = [Ui.$('content'), document.querySelector('.overview-modal')].filter(Boolean);
    if (typeof global.ResizeObserver === 'function') {
      var observer = new global.ResizeObserver(function (entries) {
        entries.forEach(function (entry) { fitStatRows(entry.target); });
      });
      targets.forEach(function (target) { observer.observe(target); });
    } else {
      global.addEventListener('resize', function () { targets.forEach(fitStatRows); });
    }
    if (document.fonts && document.fonts.ready) {
      document.fonts.ready.then(function () { targets.forEach(fitStatRows); });
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
      var entry = buildMessage(fresh[j]);
      list.insertBefore(entry, list.firstChild);
      // 面板打開時，新的訊息從清單頂端長出來；開啟案件時一次還原很多則，就直接出現。
      if (state.messagesPanelOpen && fresh.length <= 3 && global.JetMotion) { global.JetMotion.enter(entry); }
    }

    if (fresh.length > 0) {
      lastMessageId = state.messages[0].id;
    }

    while (list.children.length > state.messages.length) {
      list.removeChild(list.lastChild);
    }
  }

  // 訊息分三級（使用者 2026-10-13 裁定）：錯誤與需要注意在時間前面加一個符號，另加只給讀屏軟體的等級文字；
  // 一般說明沒有符號。.messages__text 的內容與位置不變（Gui 驅動從它找「支援日誌已輸出」）。
  var MESSAGE_LEVEL_NAMES = { error: '錯誤：', warn: '需要注意：' };

  function messageLevel(level) {
    return level === 'error' || level === 'warn' ? level : 'info';
  }

  function buildMessage(msg) {
    var level = messageLevel(msg.level);
    var li = document.createElement('li');
    li.className = 'messages__item messages__item--' + level;
    li.innerHTML =
      (MESSAGE_LEVEL_NAMES[level]
        ? '<span class="messages__mark" aria-hidden="true"></span>' +
          '<span class="visually-hidden">' + MESSAGE_LEVEL_NAMES[level] + '</span>'
        : '') +
      '<span class="messages__time">' + msg.time + '</span>' +
      '<span class="messages__text">' + Ui.esc(msg.text) + '</span>';
    return li;
  }

  // 視窗矮、左欄放不下時，訊息區黏在欄底，流程步驟從它後面捲過去；這時在左欄加 data-under-messages，
  // 讓 app.css 把訊息區變成玻璃。抽屜打開或收起、視窗改變大小與左欄捲動時都重算。純呈現，不改 state。
  // 訊息區的高度另外寫進左欄的 --messages-h，app.css 用它當 scroll-padding-bottom：用鍵盤移到第五、六步時，
  // 左欄會把它捲到訊息區上方，不會停在訊息區後面（2026-10-14 在 125% 縮放的最小視窗查到）。
  function bindMessagesEdge() {
    var column = document.querySelector('.toc-col');
    var messages = document.querySelector('.messages');
    if (!column || !messages) { return; }
    function mark() {
      column.toggleAttribute('data-under-messages', column.scrollHeight - column.scrollTop - column.clientHeight > 1);
    }
    function measure() {
      column.style.setProperty('--messages-h', messages.offsetHeight + 'px');
      mark();
    }
    if (typeof global.ResizeObserver === 'function') {
      var observer = new global.ResizeObserver(measure);
      observer.observe(column);
      observer.observe(messages);
      var nav = column.querySelector('.step-nav');
      if (nav) { observer.observe(nav); }
    } else {
      global.addEventListener('resize', measure);
    }
    column.addEventListener('scroll', mark, { passive: true });
    measure();
  }

  // 收合/展開與未讀徽章。徽章照未讀裡最嚴重的一級：有錯誤時紅底加「!」，有需要注意時琥珀底加「!」，其他維持灰底
  // （使用者 2026-10-13 裁定）；aria-label 用文字說出等級，不只靠顏色。開關固定在欄底，展開與收合是同一顆按鈕。
  function renderMessagesPanel(state) {
    var panel = document.querySelector('[data-bind="messages-panel"]');
    if (!panel) { return; }

    panel.classList.toggle('messages--collapsed', !state.messagesPanelOpen);

    var rail = panel.querySelector('.messages__rail');
    if (rail) {
      rail.setAttribute('aria-expanded', state.messagesPanelOpen ? 'true' : 'false');
      rail.title = state.messagesPanelOpen ? '收合訊息' : '展開訊息';
    }

    var badge = Ui.$('message-badge');
    if (badge) {
      badge.hidden = state.messagesUnseen === 0;
      badge.textContent = state.messagesUnseen > 99 ? '99+' : String(state.messagesUnseen);

      var worst = 'info';
      for (var i = 0; i < Math.min(state.messagesUnseen, state.messages.length); i++) {
        var level = messageLevel(state.messages[i].level);
        if (level === 'error') { worst = 'error'; break; }
        if (level === 'warn') { worst = 'warn'; }
      }
      badge.classList.toggle('messages__badge--error', worst === 'error');
      badge.classList.toggle('messages__badge--warn', worst === 'warn');
      badge.setAttribute('aria-label', state.messagesUnseen + ' 則新訊息' +
        (worst === 'error' ? '，含錯誤' : (worst === 'warn' ? '，含需要注意' : '')));
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
      // 關閉時整片淡出（app.css 的 .overview-modal[hidden]），圖表等淡完才釋放，淡出途中不會先變成一片空白。
      if (overviewWasOpen) {
        global.setTimeout(function () {
          if (!Store.getState().overviewOpen) { global.JetOverviewBi.dispose(); }
        }, motionAllowed() ? 200 : 0);
        // 關上後不再接 Escape 與 Tab（打開時在下方掛上）。
        if (overviewKeydown) { document.removeEventListener('keydown', overviewKeydown, true); }
      } else {
        global.JetOverviewBi.dispose();
      }
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
    // 開著時才在 document 接 Escape 與 Tab（見 bindOverviewDialog）。
    if (firstOpen && overviewKeydown) { document.addEventListener('keydown', overviewKeydown, true); }
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
      // 打開時焦點放在卡片的 ✕，Escape 與 Tab 從這裡接手。
      Focus.move(modal.querySelector('.overview-modal__card button[data-action="overview-close"]'));
    }
  }

  // 卡片內容重繪（開啟時的一般渲染，以及分頁切換這類純 UI 變動的即時重繪共用）。
  function renderOverviewCard(state) {
    var card = document.querySelector('.overview-modal__card');
    if (!card) { return; }
    // 總覽開著時資料或目前步驟改變會整張重畫（使用者 2026-10-13 選定處理）：焦點原本在卡片裡時，
    // 用和中欄重繪同一套認法放回同一個控制項；那個控制項不見了就放到 ✕，Escape 與 Tab 才接得住。焦點不在卡片裡時不動。
    var hadFocus = card.contains(document.activeElement);
    var focusMemory = hadFocus ? captureContentFocus(card) : null;
    global.JetOverviewBi.dispose();
    card.innerHTML = overviewHtml(state);
    fitStatRows(card);
    restoreOverviewAnalysisState(card);
    bindOverview(card);
    global.JetOverviewBi.mount(
      card,
      state.lastRuns.validate,
      state.lastRuns.prescreen,
      overviewConcentrationTab);
    if (hadFocus) {
      focusElement(findContentFocus(card, focusMemory) || card.querySelector('button[data-action="overview-close"]'));
    }
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
          '<span class="overview__col-eyebrow">GL</span>' +
        '</div>' +
        '<div class="overview-pop__grid">' +
          overviewPopKpiHtml('validation.stats.gl-row-count', 'GL 分錄數', fmtNum(stats.glRowCount), '') +
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
    if (options === Ui.GL_MODES && value === 'flag') { value = 'side'; }
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
      return overviewImportFact('import.gl.source', 'PBC-GL', imp.gl, '列') +
        overviewImportFact('import.tb.source', 'PBC-TB', imp.tb, '列') +
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
        overviewFactHtml('filter.population-scope', 'GL',
          resultRef && resultRef.populationScope === 'auditPeriod'
          ? '查核期間內的分錄'
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
      '<section class="overview-step-detail" data-motion-key="overview-step-detail" ' +
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
      var panel = Ui.findLive(card, '#overview-step-detail');
      if (panel) {
        var state = Store.getState();
        panel.className = 'overview-step-detail';
        panel.setAttribute('aria-labelledby', 'overview-stage-tab-' + index);
        // 換階段時詳情在原處交叉淡化、高度平順改變，和其他頁籤換窗格一樣（使用者 2026-10-13 要求展開與收合一致）。
        var fill = function () { panel.innerHTML = overviewStepDetailBodyHtml(state, index); };
        if (global.JetMotion) { global.JetMotion.morph(panel, fill); } else { fill(); }
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

  // 總覽開著時在 document 的捕獲階段接 Escape 與 Tab，和第四步、第五步的待確認對話框同一套（使用者 2026-10-13 選定）：
  // 焦點跑到總覽外面（例如點了卡片的空白處）時，Tab 由共用的 JetFocus.trapDialogTab 拉回總覽，Escape 也照樣關閉。
  // 作業進行中整個畫面是 inert，鍵盤留給作業遮罩的「取消作業」；待確認對話框直接加在 <body>，疊在總覽上面，開著時鍵盤交給它。
  var overviewKeydown = null;

  function overviewCoveredByDialog() {
    // 用 showModal 開在最上層的原生對話框（第五步的日期多選）也算在上面，鍵盤交給它。
    if (document.querySelector('dialog[open]')) { return true; }
    return Array.prototype.some.call(document.querySelectorAll('.modal'), function (dialog) {
      return !dialog.hidden && !dialog.inert && !dialog.closest('[data-jet-root]');
    });
  }

  function bindOverviewDialog() {
    var modal = document.querySelector('.overview-modal');
    if (!modal) { return; }

    // 背景遮罩不是控制項，依 class 綁定點擊關閉。
    var backdrop = modal.querySelector('.overview-modal__backdrop');
    if (backdrop) {
      backdrop.addEventListener('click', function () { Store.setOverviewOpen(false); });
    }

    overviewKeydown = function (event) {
      var state = Store.getState();
      if (!state.overviewOpen || state.busy || overviewCoveredByDialog()) { return; }
      if (event.key === 'Escape') {
        event.preventDefault();
        Store.setOverviewOpen(false);
        return;
      }
      Focus.trapDialogTab(modal.querySelector('.overview-modal__card'), event);
    };
  }

  function bindOverviewConcentrationTabs(card) {
    var tabs = Array.prototype.slice.call(card.querySelectorAll('[data-overview-tab]'));
    // 只換分頁底下的內容與圖表：分頁的選取滑過去，內容在原處交叉淡化、高度平順改變，
    // 不再整張卡片重畫、所有圖表重來一次（使用者 2026-10-13 要求展開與收合一致）。
    function select(key) {
      if (key !== 'preparers' && key !== 'accounts') { return; }
      overviewConcentrationTab = key;
      var state = Store.getState();
      var panel = Ui.findLive(card, '#overview-concentration-panel');
      var template = document.createElement('template');
      template.innerHTML = overviewHtml(state);
      var fresh = template.content.querySelector('#overview-concentration-panel');
      if (!panel || !fresh) {
        renderOverviewCard(state);
      } else {
        tabs.forEach(function (tab) {
          var active = tab.getAttribute('data-overview-tab') === key;
          tab.classList.toggle('overview-bi__tab--active', active);
          tab.setAttribute('aria-selected', active ? 'true' : 'false');
          tab.setAttribute('tabindex', active ? '0' : '-1');
        });
        panel.setAttribute('aria-labelledby', fresh.getAttribute('aria-labelledby'));
        global.JetOverviewBi.disposeWithin(panel);
        var fill = function () { panel.innerHTML = fresh.innerHTML; };
        if (global.JetMotion) { global.JetMotion.morph(panel, fill); } else { fill(); }
        global.JetOverviewBi.mount(panel, state.lastRuns.validate, state.lastRuns.prescreen, overviewConcentrationTab);
      }
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
          // 收起時圖表跟著內容收回標題（app.css 的 ::details-content），收完才釋放，收的途中不會先變成一片空白。
          global.setTimeout(function () {
            if (!section.open) { global.JetOverviewBi.disposeWithin(section); }
          }, motionAllowed() ? 450 : 0);
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
      preparingData: '正在準備資料',
      writingSheet: '正在寫入',
      finalizingWorkbook: '正在完成檔案',
      publishingArtifact: '正在存進案件資料夾'
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

    // 每個階段一句（使用者 2026-10-13 勾選頁確認 PROGRESS，用第一輪的寫法）；準備資料時還沒有工作表與列數。
    var phaseText = phaseLabels[progress.phase] + (isWritingSheet ? '「' + progress.sheetName + '」' : '');
    var counts = progress.phase === 'preparingData' ? ''
      : '，已完成 ' + sheetsCompleted.toLocaleString('zh-Hant') + ' 張工作表、寫入 ' +
        rowsWritten.toLocaleString('zh-Hant') + ' 列';
    return artifactLabels[progress.artifactKind] + '：' + phaseText + counts +
      '，' + Ui.elapsedPhrase('經過', elapsedMilliseconds);
  }

  /* ---- 按下的光（純呈現） -------------------------------------------------------
     按下藍色主要按鈕時，光從按下的位置亮起來，放開後淡掉（使用者 2026-10-11 要求依 Apple Liquid Glass 的理念調整：
     材質在被按下時從裡面亮起來，告訴使用者按到了）。這裡只記下按下的位置，亮光由 app.css 的 :active::after 畫出；
     用鍵盤按下時清掉位置，光從按鈕中央亮起。只寫 CSS 變數，不改 state、不送 action。 */
  function bindPressLight() {
    document.addEventListener('pointerdown', function (event) {
      if (event.button !== 0) { return; }
      var target = event.target && event.target.closest ? event.target.closest('.btn') : null;
      if (!target) { return; }
      var rect = target.getBoundingClientRect();
      target.style.setProperty('--press-x', Math.round(event.clientX - rect.left) + 'px');
      target.style.setProperty('--press-y', Math.round(event.clientY - rect.top) + 'px');
    }, { capture: true, passive: true });
    document.addEventListener('keydown', function (event) {
      if (event.key !== ' ' && event.key !== 'Enter') { return; }
      var target = event.target && event.target.closest ? event.target.closest('.btn') : null;
      if (!target) { return; }
      target.style.removeProperty('--press-x');
      target.style.removeProperty('--press-y');
    }, true);
  }

  /* ---- 玻璃標題列的高度（純呈現） -------------------------------------------------
     使用者 2026-10-12 要求整個 JE Tool 帶有 Liquid Glass 的質感，所以標題列是一片玻璃，流程欄、中欄、資料預覽與案件清單
     都從視窗頂端開始，上緣藏在標題列下面。標題列一列或兩列的高度不同，這裡量出它的下緣，寫入 .app-shell 的 --chrome-h，
     app.css 依它讓出各欄的上內距與 scroll-padding-top。內容捲到標題列下面時，在 .app-shell 加 data-under-chrome，
     標題列的影子加深。只改呈現，不改 state、不送 action。 */
  function bindChromeHeight() {
    var shell = document.querySelector('.app-shell');
    var header = document.querySelector('.app-header');
    var summary = document.querySelector('.case-summary');
    if (!shell || !header) { return; }

    // 目前看得到的捲動區有沒有內容捲到標題列下面；換畫面時也要重算，免得影子留在沒有捲動的畫面上。
    function markUnder() {
      var scrolled = Array.prototype.some.call(document.querySelectorAll('.content, .project-picker, .toc-col'), function (scroller) {
        return scroller.getClientRects().length > 0 && scroller.scrollTop > 1;
      });
      shell.toggleAttribute('data-under-chrome', scrolled);
    }

    function measure() {
      var bottom = header.getBoundingClientRect().bottom;
      if (summary && summary.getClientRects().length) {
        var rect = summary.getBoundingClientRect();
        if (rect.height > 0) { bottom = Math.max(bottom, rect.bottom); }
      }
      var height = Math.round(bottom - shell.getBoundingClientRect().top);
      if (height > 0) { shell.style.setProperty('--chrome-h', height + 'px'); }
      markUnder();
    }

    if (typeof global.ResizeObserver === 'function') {
      var observer = new global.ResizeObserver(measure);
      observer.observe(header);
      if (summary) { observer.observe(summary); }
    } else {
      global.addEventListener('resize', measure);
    }
    measure();

    document.addEventListener('scroll', function (event) {
      var scroller = event.target;
      if (!scroller || typeof scroller.matches !== 'function' || !scroller.matches('.content, .project-picker, .toc-col')) { return; }
      markUnder();
    }, { capture: true, passive: true });
  }

  /* ---- 選取底的滑動（純呈現） ---------------------------------------------------
     換選取時，選取底從原本的位置滑到新的位置，讓人看到選取移到哪裡（Apple 的空間一致性）。
     這裡量出每組目前選中的那一格，寫入 --glide-x、--glide-y、--glide-w、--glide-h，並在組上加 .has-glide；
     選取底由 app.css 的 .has-glide::before 畫。很多選項組換選取時會整段重繪，所以每組記住上一次的位置：
     同一批重繪裡在原地換上的新組，從舊的一組此刻看到的位置滑過去；一組消失後再出現、出現在別的位置，或視窗大小與字型改變時，
     就直接放好，不滑動。Windows 高對比佈景不加 .has-glide，選中的樣式由 app.css 用系統色畫。
     使用者 2026-10-12 要求整個 JE Tool 帶有 Liquid Glass 的質感，所以滑動照 Apple 控制項的做法：
     選取在滑動時浮起來變成玻璃（組的 ::after），朝目的地拉長，前緣先到、後緣跟上，到位後落回原本的一片（組的 ::before）。
     底線頁籤（line: true）只有底線滑過去，也是前緣先到。路徑在這裡算好交給瀏覽器播放，滑到一半又重繪時，從當下看到的形狀接著滑。
     流程欄（rigid: true）不浮起也不拉長：使用者 2026-10-12 認為放大縮小過於醒目，要求維持原本的樣子與相同的寬度，
     所以那一片照原樣、同樣大小平移到新的步驟。
     選中的字也跟著選取一起換：目的地的字等選取快到時才變粗、變色，原本的字在選取離開時就變回一般（item 是選項，text 是要換樣子的字）。
     減少動態時直接放到新的位置。只改呈現，不改 state、不送 action。 */
  var GLIDE_GROUPS = [
    { group: '.step-nav', item: '.toc-item', text: '.toc-item__label', active: '.toc-item--current', rigid: true },
    { group: '.seg-toggle', item: '.seg-toggle__btn', active: '.seg-toggle__btn.is-active' },
    { group: '.filter-source-tabs', item: 'button', active: 'button[aria-pressed="true"]' },
    { group: '.filter-matrix-tabs', item: 'button', active: 'button[aria-pressed="true"]' },
    { group: '.overview-bi__tabs', item: '.overview-bi__tab', active: '.overview-bi__tab--active' },
    { group: '.mapping-section > .mode-group', item: '.mode-option', active: '.mode-option:has(:checked)' },
    { group: '.filter-work-tabs', item: 'button', active: 'button[aria-pressed="true"]', line: true },
    { group: '.account-methods', item: 'button', active: 'button[aria-selected="true"]', line: true },
    { group: '.data-preview__tabs', item: '.data-preview__tab', active: '.data-preview__tab.is-active', line: true }
  ];

  function bindGlide() {
    if (typeof global.MutationObserver !== 'function' || typeof global.requestAnimationFrame !== 'function') { return; }
    var forcedColors = typeof global.matchMedia === 'function' ? global.matchMedia('(forced-colors: active)') : null;
    var memory = {};
    var frame = 0;
    var instant = false;
    var instantWithin = [];   // 寬度剛改變的欄：裡面的選取直接放好，其他欄照常滑動
    var observer;

    // 組在版面上的位置，不受捲動影響；用來判斷重繪後的新組是不是在原地換上的。
    function layoutPosition(element) {
      var left = 0;
      var top = 0;
      for (var node = element; node; node = node.offsetParent) {
        left += node.offsetLeft;
        top += node.offsetTop;
      }
      return { left: left, top: top };
    }

    // 每一緣各是一條彈簧，帶著自己的速度：滑到一半改去別的地方時，從此刻的位置與速度接著走（WWDC23「Animate with springs」）。
    // 選取像一滴水移過去：朝目的地的那一緣用快一點的彈簧先到，另一緣跟上，中途稍微拉長、到了收回原本的大小。
    // 流程欄（rigid）四緣用同一條彈簧，大小相同時整片平移，不拉長也不縮放。
    // 分段切換的選取在滑動時浮起成玻璃，用玻璃的彈簧（bounce 0.15）；流程欄與底線頁籤屬於內容層，不回彈。
    function glideMotion(from, to, carry, spec, now) {
      var forwardX = to.x + to.w / 2 >= from.x + from.w / 2;
      var forwardY = to.y + to.h / 2 >= from.y + from.h / 2;
      var starts = [from.x, from.x + from.w, from.y, from.y + from.h];
      var ends = [to.x, to.x + to.w, to.y, to.y + to.h];
      var leads = spec.rigid ? [false, false, false, false] : [!forwardX, forwardX, !forwardY, forwardY];
      var glass = !spec.rigid && !spec.line;
      var main = glass ? SPRINGS.glassGlide : SPRINGS.glide;
      var quick = glass ? SPRINGS.glassLead : SPRINGS.lead;
      var edges = starts.map(function (value, index) {
        var velocity = carry.velocity ? carry.velocity[index] : 0;
        return springSegment(leads[index] ? quick : main, value, ends[index], velocity, now);
      });
      return {
        from: from, to: to, start: now, edges: edges, lifted: !!carry.lifted, rigid: !!spec.rigid,
        duration: Math.max.apply(null, edges.map(function (edge) { return edge.duration; }).concat([1])),
        // 看起來到了的時間：選取在這時落回原本的一片、字也換好。
        arrive: main.duration * 1000
      };
    }

    function shapeAt(motion, now) {
      if (now - motion.start >= motion.duration) { return motion.to; }
      var edges = motion.edges.map(function (edge) { return edge.at(now)[0]; });
      return { x: edges[0], y: edges[2], w: Math.max(edges[1] - edges[0], 0), h: Math.max(edges[3] - edges[2], 0) };
    }

    function velocityAt(motion, now) {
      return motion.edges.map(function (edge) { return edge.at(now)[1]; });
    }

    function visibleRect(entry, now) {
      if (!entry) { return null; }
      return entry.motion ? shapeAt(entry.motion, now) : entry.rect;
    }

    function sameRect(a, b) {
      return Math.round(a.x) === Math.round(b.x) && Math.round(a.y) === Math.round(b.y) &&
        Math.round(a.w) === Math.round(b.w) && Math.round(a.h) === Math.round(b.h);
    }

    function place(group, rect) {
      group.style.setProperty('--glide-x', rect.x + 'px');
      group.style.setProperty('--glide-y', rect.y + 'px');
      group.style.setProperty('--glide-w', rect.w + 'px');
      group.style.setProperty('--glide-h', rect.h + 'px');
    }

    function stop(group) {
      (group.__glideAnimations || []).forEach(function (animation) { animation.cancel(); });
      group.__glideAnimations = [];
    }

    function px(value) { return Math.round(value * 10) / 10 + 'px'; }

    // 字的樣子只看顏色與字重；兩種字型都是可變字重，所以字重可以平順地變。
    function textLook(element) {
      var style = global.getComputedStyle(element);
      return { color: style.color, fontWeight: style.fontWeight };
    }

    // 組裡位置與大小和 rect 相同的那一個選項；滑到一半轉向時，起點不在任何選項上，就沒有。
    function itemAt(group, spec, rect) {
      var items = spec.item ? group.querySelectorAll(spec.item) : [];
      for (var i = 0; i < items.length; i += 1) {
        if (Math.abs(items[i].offsetLeft - rect.x) < 2 && Math.abs(items[i].offsetTop - rect.y) < 2 &&
            Math.abs(items[i].offsetWidth - rect.w) < 2 && Math.abs(items[i].offsetHeight - rect.h) < 2) { return items[i]; }
      }
      return null;
    }

    function textOf(spec, item) { return item && spec.text ? item.querySelector(spec.text) : item; }

    // 字正在變的選項此刻的樣子；要在取消動畫之前讀。
    function textInMotion(group) {
      var looks = new global.Map();
      (group.__glideAnimations || []).forEach(function (animation) {
        if (animation.__glideText && animation.playState === 'running') { looks.set(animation.__glideText, textLook(animation.__glideText)); }
      });
      return looks;
    }

    // 原本那一格的字在選取離開時就變回一般；目的地的字等選取快到時才變粗、變色，和選取一起到。
    // 滑到一半改去別的地方時，字從此刻的樣子接著變：剛才的目的地退回一般，新的目的地仍等選取快到才變，不會先到。
    function animateText(group, spec, motion, options, prior) {
      var active = group.querySelector(spec.active);
      var activeText = textOf(spec, active);
      var items = spec.item ? Array.prototype.slice.call(group.querySelectorAll(spec.item)) : [];
      var restText = textOf(spec, items.filter(function (item) { return item !== active; })[0]);
      if (!activeText || !restText) { return []; }
      var chosen = textLook(activeText);
      var rest = textLook(restText);
      if (chosen.color === rest.color && chosen.fontWeight === rest.fontWeight) { return []; }
      var source = itemAt(group, spec, motion.from);
      var sourceText = source && source !== active ? textOf(spec, source) : null;
      var start = prior.get(activeText) || rest;
      var arrive = Math.min(motion.arrive / motion.duration, 1);
      var animations = [];
      function keep(element, animation) { animation.__glideText = element; animations.push(animation); }
      if (source !== active) {
        keep(activeText, activeText.animate([
          { offset: 0, color: start.color, fontWeight: start.fontWeight },
          { offset: arrive * 0.45, color: start.color, fontWeight: start.fontWeight, easing: 'ease-out' },
          { offset: arrive * 0.95, color: chosen.color, fontWeight: chosen.fontWeight },
          { offset: 1, color: chosen.color, fontWeight: chosen.fontWeight }
        ], options));
      }
      var leaving = new global.Map();
      if (sourceText) { leaving.set(sourceText, prior.get(sourceText) || chosen); }
      prior.forEach(function (look, element) {
        if (element !== activeText && element.isConnected && !leaving.has(element)) { leaving.set(element, look); }
      });
      leaving.forEach(function (look, element) {
        keep(element, element.animate([
          { offset: 0, color: look.color, fontWeight: look.fontWeight, easing: 'ease-out' },
          { offset: arrive * 0.5, color: rest.color, fontWeight: rest.fontWeight },
          { offset: 1, color: rest.color, fontWeight: rest.fontWeight }
        ], options));
      });
      return animations;
    }

    // 底線頁籤只有底線滑過去；流程欄的那一片同樣大小平移；分段切換的選取在滑動時浮起來變成玻璃，到了再落回原本的一片。
    // motion.lifted 表示滑到一半就改去別的地方：玻璃已經浮起來，直接從當下的形狀轉向，不先閃回實心的一片。
    // elapsed 大於 0 時從那個進度接著播，用在整組被重繪換掉、但目的地沒變的時候。
    function animate(group, spec, motion, elapsed) {
      var prior = textInMotion(group);
      stop(group);
      if (!motionAllowed() || !canAnimate()) { return; }
      var total = motion.duration;
      var count = Math.max(Math.ceil(total / 16), 8);
      var shapes = [];
      for (var i = 0; i <= count; i += 1) {
        shapes.push({ offset: i / count, time: i / count * total, shape: shapeAt(motion, motion.start + i / count * total) });
      }
      var options = { duration: total, easing: 'linear' };
      var animations = [];
      if (spec.line) {
        animations.push(group.animate(shapes.map(function (frame) {
          return { offset: frame.offset, transform: 'translate(' + px(frame.shape.x) + ', ' + px(frame.shape.y + frame.shape.h - 2) + ')', width: px(frame.shape.w) };
        }), Object.assign({ pseudoElement: '::before' }, options)));
      } else if (motion.rigid) {
        // 大小沒變時只動位置，瀏覽器可以在合成層播放：換步驟時中欄重繪很重，這一片也不會停頓後一下跳到半路。
        var resize = Math.round(motion.from.w) !== Math.round(motion.to.w) || Math.round(motion.from.h) !== Math.round(motion.to.h);
        animations.push(group.animate(shapes.map(function (frame) {
          var key = { offset: frame.offset, transform: 'translate(' + px(frame.shape.x) + ', ' + px(frame.shape.y) + ')' };
          if (resize) { key.width = px(frame.shape.w); key.height = px(frame.shape.h); }
          return key;
        }), Object.assign({ pseudoElement: '::before' }, options)));
      } else {
        // 玻璃（組的 ::after）先浮起來蓋住原本的一片，原本的一片才淡掉；快到時目的地的一片先在玻璃底下出現，玻璃再淡掉。
        // 兩片交接時互相蓋著，中間不會空一下。浮起只放大 2.5%，滑到一半最高、到了收回（Apple 讓觸控板操作的反應比直接觸控收斂）。
        var arrive = motion.arrive;
        var lens = shapes.map(function (frame) {
          var time = frame.time;
          var bump = Math.sin(Math.PI * Math.min(time / arrive, 1));
          var rise = motion.lifted ? 1 : Math.min(time / 60, 1);
          var fall = time <= arrive - 30 ? 1 : Math.max(1 - (time - arrive + 30) / 140, 0);
          return {
            offset: frame.offset,
            transform: 'translate(' + px(frame.shape.x) + ', ' + px(frame.shape.y) + ') scale(' + (1 + 0.025 * bump).toFixed(4) + ')',
            width: px(frame.shape.w),
            height: px(frame.shape.h),
            opacity: (rise * fall).toFixed(3)
          };
        });
        var from = motion.from;
        var to = motion.to;
        function at(rect, time, opacity) {
          return {
            offset: Math.min(Math.max(time / total, 0), 1),
            transform: 'translate(' + px(rect.x) + ', ' + px(rect.y) + ')', width: px(rect.w), height: px(rect.h), opacity: opacity
          };
        }
        var show = Math.min(Math.max(arrive - 90, 120), total);
        var shown = Math.min(arrive + 30, total);
        var puck = motion.lifted
          ? [at(to, 0, 0), at(to, show, 0), at(to, shown, 1), at(to, total, 1)]
          : [at(from, 0, 1), at(from, 40, 1), at(from, 110, 0), at(to, 110, 0), at(to, show, 0), at(to, shown, 1), at(to, total, 1)];
        animations.push(group.animate(puck, Object.assign({ pseudoElement: '::before' }, options)));
        animations.push(group.animate(lens, Object.assign({ pseudoElement: '::after' }, options)));
      }
      animations = animations.concat(animateText(group, spec, motion, options, prior));
      if (elapsed > 0) {
        animations.forEach(function (animation) { animation.currentTime = elapsed; });
      }
      group.__glideAnimations = animations;
    }

    function sync() {
      var jumpAll = instant;
      var resized = instantWithin;
      instantWithin = [];
      var seen = {};
      var disabled = !!(forcedColors && forcedColors.matches);
      var now = global.performance && typeof global.performance.now === 'function' ? global.performance.now() : Date.now();
      frame = 0;
      instant = false;
      GLIDE_GROUPS.forEach(function (spec, specIndex) {
        // 正在收起的舊畫面（JetMotion 放回去的舊元素）只是畫面，不算一組，也不佔順序。
        var groups = Array.prototype.filter.call(document.querySelectorAll(spec.group), function (group) {
          return !group.closest('[data-motion-leaving]');
        });
        groups.forEach(function (group, ordinal) {
          var jump = jumpAll || resized.some(function (column) { return column.contains(group); });
          var item = group.querySelector(spec.active);
          if (disabled || !item || !item.offsetWidth) {
            stop(group);
            group.classList.remove('has-glide');
            return;
          }
          var key = specIndex + '|' + (group.getAttribute('aria-label') || '') + '|' + ordinal;
          var rect = { x: item.offsetLeft, y: item.offsetTop, w: item.offsetWidth, h: item.offsetHeight };
          var at = layoutPosition(group);
          var last = memory[key];
          var holder = group.closest('[data-motion-key]');
          var owner = holder ? holder.getAttribute('data-motion-key') : null;
          var entry = { group: group, rect: rect, at: at, owner: owner, motion: null };
          var fresh = !group.classList.contains('has-glide');
          // 同一批重繪剛換掉舊的一組：新的一組接著舊的一組此刻看到的樣子。屬於同一個區塊（同一個 data-motion-key）就是同一組，
          // 即使上方同時多了提示、整組被往下推也一樣（使用者 2026-10-12 指出第三步換金額欄位格式時選取直接跳過去）；
          // 沒有區塊識別的組，才用「在原地換上」判斷。選取畫在組自己的座標裡，組被推動時選取跟著組走。
          // 舊的一組在交叉淡化的舊畫面裡時，雖然還在頁面上，也算已經換掉。
          var replaced = fresh && !jump && last && last.group !== group &&
            (!last.group.isConnected || !!last.group.closest('[data-motion-leaving]')) &&
            (owner ? last.owner === owner
              : Math.abs(last.at.left - at.left) < 2 && Math.abs(last.at.top - at.top) < 2);
          var moving = !!(last && last.motion && now - last.motion.start < last.motion.duration);
          var start = null;
          seen[key] = true;
          if (fresh) { group.classList.add('has-glide'); }
          place(group, rect);
          if (jump) {
            stop(group);
          } else if (moving && (replaced || last.group === group) && sameRect(last.motion.to, rect)) {
            // 目的地沒變：同一組繼續原本的滑動，不重新起跑；被換掉的新組從原本的進度接著播。
            entry.motion = last.motion;
            if (last.group !== group) { animate(group, spec, last.motion, now - last.motion.start); }
          } else {
            if (replaced || (!fresh && last)) { start = visibleRect(last, now); }
            if (start && !sameRect(start, rect)) {
              // 滑到一半改去別的地方：從此刻的形狀與速度接著走，選取已經浮起來，不先落回去。
              var velocity = moving ? velocityAt(last.motion, now) : null;
              entry.motion = glideMotion(start, rect, { velocity: velocity, lifted: moving }, spec, now);
              animate(group, spec, entry.motion, 0);
            }
          }
          memory[key] = entry;
        });
      });
      Object.keys(memory).forEach(function (key) {
        if (!seen[key]) { delete memory[key]; }
      });
      if (observer) { observer.takeRecords(); }
    }

    function schedule() {
      if (!frame) { frame = global.requestAnimationFrame(sync); }
    }

    function scheduleInstantly() {
      instant = true;
      schedule();
    }

    observer = new global.MutationObserver(schedule);
    observer.observe(document.body, {
      subtree: true,
      childList: true,
      attributes: true,
      attributeFilter: ['class', 'aria-pressed', 'aria-selected', 'hidden']
    });
    document.addEventListener('change', schedule, true);
    global.addEventListener('resize', scheduleInstantly);
    // 中欄與右側資料預覽的寬度在收合、展開資料預覽時沿彈簧變（app.css 的 .data-preview），選取跟著每一格放好，不在播完之後才跳。
    if (typeof global.ResizeObserver === 'function') {
      var columns = new global.ResizeObserver(function (entries) {
        entries.forEach(function (entry) { instantWithin.push(entry.target); });
        schedule();
      });
      ['.content', '.data-preview'].forEach(function (selector) {
        var column = document.querySelector(selector);
        if (column) { columns.observe(column); }
      });
    }
    if (document.fonts && typeof document.fonts.addEventListener === 'function') {
      document.fonts.addEventListener('loadingdone', scheduleInstantly);
    }
    if (forcedColors && typeof forcedColors.addEventListener === 'function') {
      forcedColors.addEventListener('change', schedule);
    }
    schedule();
  }

  /* ---- 動態共用的設定（純呈現） -------------------------------------------------------------
     使用者 2026-10-12 認為動態仍然突兀，要求先研究 Apple 的設計理念、人機介面指引與開發者文件再重做。
     整個 JE Tool 的動態改用 Apple 的彈簧（WWDC23「Animate with springs」）：duration 是看起來完成的時間，bounce 0 不回彈；
     stiffness 是 (2π ÷ duration)²，阻尼比是 1 − bounce。被下一個操作打斷時，從此刻的位置與速度接著走向新的目標，
     不會頓一下再從零速重新起跑。人機介面指引 Motion 說 Liquid Glass 用觸控板操作時的反應比直接觸控收斂，用滑鼠操作的 JET 跟著收斂，所以時間比 Apple 預設的 0.5 秒短一點。
     app.css 的 --ease-spring 與 --motion-* 是同一組參數算出來的，兩邊要一起改。
     使用者 2026-10-13 認為淡入淡出太慢，所以長出來與收起來再縮短一點，字的淡入淡出也改成更早開始、更快完成。
     同一天要求移除或清除東西時，要收起的那一個先「類似蘋果那樣輕微快速地晃動」再收起來：shake 是一條會來回的彈簧，
     從原位帶著速度出發，左右晃兩下半就停（約 0.2 秒），見下面 JetMotion 的 removing。 */
  // 使用者 2026-10-15 認為進階條件篩選的動態拖累操作，要求加快但不偏離原本的風格。不回彈的彈簧形狀都一樣、只差時間長短，
  // 所以全部同比例縮短約兩成，各動態之間的節奏不變。選取與帶過去的標籤仍約 0.3 秒，看得出從哪裡到哪裡，
  // 不會一閃而過；收起的字先淡出，不需要看清去向，所以可以短到 0.22 秒。
  // 同一天使用者澄清 2026-10-13 不喜歡的是拋物線軌跡、不是回彈，並要求完整依 Apple 的 Liquid Glass 設計重新整理。
  // Apple 的玻璃在互動時帶一點彈性，「減少動態效果」才把彈性關掉，所以動態分成兩種性格（使用者同日選「直接照建議改」）：
  // - 玻璃層被操作的東西用 bounce 0.15，接近 Apple 的 snappy：分段切換的玻璃選取、加入時帶過去的玻璃標籤，
  //   以及 app.css 的選單、對話框浮現與按鍵放開。看不出在彈，只是早一點到、收尾俐落，最多超過目標 0.6%。
  // - 內容層的版面改變維持 bounce 0：長出來、收起來、原地換掉、高度改變，以及流程欄與底線頁籤的選取。
  var SPRINGS = {
    glide: { duration: 0.28, bounce: 0 },          // 流程欄與底線頁籤的選取滑到新的位置
    lead: { duration: 0.21, bounce: 0 },           // 底線頁籤朝目的地的那一緣，比另一緣先到一點
    glassGlide: { duration: 0.28, bounce: 0.15 },  // 分段切換的選取浮起成玻璃，滑到新的位置
    glassLead: { duration: 0.21, bounce: 0.15 },   // 玻璃選取朝目的地的那一緣，比另一緣先到一點
    open: { duration: 0.28, bounce: 0 },           // 長出來、展開、原地換掉、高度改變
    close: { duration: 0.22, bounce: 0 },          // 收起來
    carry: { duration: 0.30, bounce: 0.15 },       // 加入時帶過去的玻璃標籤與中欄的捲動
    shake: { duration: 0.11, bounce: 0.75 }        // 移除前左右晃一下
  };

  function motionAllowed() {
    if (typeof global.matchMedia !== 'function') { return true; }
    return !global.matchMedia('(prefers-reduced-motion: reduce)').matches &&
      !global.matchMedia('(forced-colors: active)').matches;
  }

  function canAnimate() {
    return typeof global.requestAnimationFrame === 'function' && typeof global.Element === 'function' &&
      typeof global.Element.prototype.animate === 'function';
  }

  function springOf(preset) {
    if (!preset.w0) {
      preset.w0 = 2 * Math.PI / preset.duration;
      preset.zeta = preset.bounce >= 0 ? 1 - preset.bounce : 1 / (1 + preset.bounce);
    }
    return preset;
  }

  // 彈簧此刻的位移與速度：x0 是起點減終點，v0 是初速（每秒），t 是經過的秒數。三種阻尼各有公式解，不逐格累加，所以畫面快慢不影響結果。
  function springState(preset, x0, v0, t) {
    var spring = springOf(preset);
    var w0 = spring.w0;
    var zeta = spring.zeta;
    if (Math.abs(1 - zeta) < 1e-6) {
      var b = v0 + w0 * x0;
      var decay = Math.exp(-w0 * t);
      return [decay * (x0 + b * t), decay * (v0 - w0 * b * t)];
    }
    if (zeta < 1) {
      var wd = w0 * Math.sqrt(1 - zeta * zeta);
      var envelope = Math.exp(-zeta * w0 * t);
      var cos = Math.cos(wd * t);
      var sin = Math.sin(wd * t);
      return [envelope * (x0 * cos + ((v0 + zeta * w0 * x0) / wd) * sin),
        envelope * (v0 * cos - ((w0 * w0 * x0 + zeta * w0 * v0) / wd) * sin)];
    }
    var q = Math.sqrt(zeta * zeta - 1);
    var r1 = -w0 * (zeta - q);
    var r2 = -w0 * (zeta + q);
    var a = (v0 - r2 * x0) / (r1 - r2);
    var rest = x0 - a;
    return [a * Math.exp(r1 * t) + rest * Math.exp(r2 * t), r1 * a * Math.exp(r1 * t) + r2 * rest * Math.exp(r2 * t)];
  }

  // 彈簧真的停下來的時間（毫秒）：之後位置與速度都小於這段距離的千分之一。
  function springSettle(preset, x0, v0) {
    var spring = springOf(preset);
    var scale = Math.max(Math.abs(x0), Math.abs(v0) / spring.w0, 1e-6);
    var last = 0;
    for (var t = 0.004; t < 3; t += 0.004) {
      var state = springState(spring, x0, v0, t);
      if (Math.abs(state[0]) > 0.001 * scale || Math.abs(state[1]) / spring.w0 > 0.001 * scale) { last = t; } else if (t - last > 0.15) { break; }
    }
    return Math.round((last + 0.004) * 1000);
  }

  // 一段彈簧：值從 from 走向 to，初速 velocity（每秒）。at(now) 回傳此刻的值與速度；duration 是停下來的時間。
  function springSegment(preset, from, to, velocity, start) {
    var x0 = from - to;
    var v0 = velocity || 0;
    var duration = x0 === 0 && v0 === 0 ? 0 : springSettle(preset, x0, v0);
    return {
      from: from, to: to, start: start, duration: duration,
      at: function (now) {
        var elapsed = now - start;
        if (elapsed <= 0) { return [from, v0]; }
        if (elapsed >= duration) { return [to, 0]; }
        var state = springState(preset, x0, v0, elapsed / 1000);
        return [to + state[0], state[1]];
      }
    };
  }

  // 從靜止開始的彈簧在 elapsed 毫秒時走了全程的多少；給只需要一條曲線的地方用。
  function springProgress(preset, elapsed) {
    if (elapsed <= 0) { return 0; }
    return 1 + springState(preset, -1, 0, elapsed / 1000)[0];
  }

  function motionNow() {
    return global.performance && typeof global.performance.now === 'function' ? global.performance.now() : Date.now();
  }

  // 正由標籤帶過去的新一列（見 bindArrival）；JetMotion.carries 用它回答移焦點的一方要不要自己捲過去。
  var carried = typeof global.WeakSet === 'function' ? new global.WeakSet() : null;

  // 第一個符合的觸發元件與它的設定；沒有就回傳 null。
  function matchTrigger(target, specs) {
    if (!target || !target.closest) { return null; }
    for (var i = 0; i < specs.length; i += 1) {
      var trigger = target.closest(specs[i].trigger);
      if (trigger) { return { spec: specs[i], trigger: trigger }; }
    }
    return null;
  }

  /* ---- 加入與移除的動態（JetMotion）：加入、移除、清除與換掉的東西長出來、收起來或原地改變（純呈現） ----------------
     使用者 2026-10-12 指出只有加入條件有動態，移除、清除條件時沒有，其他步驟也一樣，操作起來不一致。
     畫面多半是整段重繪，所以重繪前記下要比對的東西（capture），重繪後比對（play）：
     - 新出現的從 0 長到自己的大小，字等空間打開大半才淡入；
     - 消失的，把重繪前的那一個放回原本的位置收到 0，字先淡出，收完才拿掉；後面的內容跟著收上來，不會一次跳上去。
       放回去的舊元素不能按、讀屏軟體不唸，也拿掉 id、data-action 這類會被程式找到的識別；
     - 長到一半或收到一半又重繪的，從此刻的樣子與速度接著播，連續快速操作也不會跳；
     - 外層也是新出現或消失時，只動最外層的那一個。data-motion-list 標出清單容器，清單裡的東西全部消失時仍放得回去。
     使用者同一天再指出第三步切換金額欄位格式時選取一閃而過、提示直接跳出來，並要求動態更有質感、更自然，全部流程畫面一致。
     所以除了標了 data-motion-key 的東西，也比對它們底下直接一層、沒有自己識別的區塊，並補上原地的改變：
     - 同一個東西重繪後高度變了，沿彈簧從原本的高度變成新的高度，下面的內容跟著平順移動；
     - 同一個東西重繪後自己的字變了，或同一個位置舊的換成新的（例如頁籤換窗格），在原處交叉淡化，不再一閃而過；
     - 裡面還有東西在長出來、收起來或改變時，交給裡面的處理，外層的高度跟著自然改變；
       裡面沒有接住的改變（例如表格換了列數），由內往外補上外層的高度改變。
     - 內容差很多的整塊（高過 THROUGH_HEIGHT，或頁籤換窗格的 swap）先淡掉舊的、再淡入新的，兩種內容不疊在一起。
     長出來、收起來與原地改變都用上面的 SPRINGS（Apple 的彈簧），被下一個操作打斷時帶著此刻的速度接著走。
     正在輸入文字時不交叉淡化，免得每打一個字就閃一次；捲動容器本身不改高度，免得捲軸位置跑掉；
     比一個畫面還高的區塊只改高度、不交叉淡化，免得整頁疊成兩層。
     data-motion="fade" 只淡入淡出、不改大小，消失時在原處淡掉，用在整塊替換的窗格；data-motion="inline" 改成左右收放，用在一排的小標籤。
     表格列不標，因為列不能縮到 0，表格交給包住它的區塊。換步驟這類整塊換掉的重繪由呼叫的一方決定不比對；
     減少動態與 Windows 高對比佈景時直接換。
     原地切換 hidden 的展開區不經過這裡，由 app.css 的 [data-collapsible] 處理；重繪期間 data-motion-rendering 讓它們不要重播。
     只改呈現：不改 state、不送 action、不移動焦點與捲軸。 */
  // 局部更新只查找仍在使用的元素；淡出複本及其子元素都屬於上一個畫面。
  function findLiveElement(root, selector) {
    if (!root) { return null; }
    var elements = root.querySelectorAll(selector);
    for (var i = 0; i < elements.length; i += 1) {
      if (!elements[i].closest('[data-motion-leaving]')) { return elements[i]; }
    }
    return null;
  }

  var Motion = (function () {
    // 舊複本只留下樣式用得到的 data- 屬性，其餘拿掉，程式與測試才找不到它。樣式要用的識別優先掛在 class 上；
    // app.css 用到這裡沒留的屬性時，架構測試 DesignIntegrationFrontendTests 會失敗（使用者 2026-10-15 指出複本露出原形）。
    var KEEP_ON_LEAVING = /^data-(?:motion$|motion-key$|kct-letter$|step-index$|child-|join$|period-end-days$)/;
    var DROP_ON_LEAVING = ['id', 'name', 'for', 'form', 'aria-controls', 'aria-describedby', 'aria-labelledby',
      'aria-owns', 'aria-activedescendant', 'aria-live', 'tabindex'];
    var BLOCK = ['height', 'paddingTop', 'paddingBottom', 'borderTopWidth', 'borderBottomWidth', 'marginTop', 'marginBottom'];
    var INLINE = ['width', 'paddingLeft', 'paddingRight', 'borderLeftWidth', 'borderRightWidth', 'marginLeft', 'marginRight'];
    // 表格本身與它的列、格不能收到 0，也不能用高度裁切，交給包住表格的區塊處理。
    var UNSIZED = /^(?:TABLE|THEAD|TBODY|TFOOT|TR|TD|TH|COL|COLGROUP|CAPTION|SCRIPT|STYLE|TEMPLATE)$/;
    var MOVED = '::moved';  // 換了順序的東西，舊位置那一份的識別
    // 原地交叉淡化：新的疊在舊的上面很快淡入，舊的在底下晚一點才淡掉。沒變的字兩份疊在一起，看起來一直是實的，
    // 只有變了的地方看得到舊的淡掉、新的淡入。使用者 2026-10-13 認為淡入淡出太慢，所以全部在 0.15 秒左右完成。
    var FADE_IN = 100;
    var FADE_OUT_DELAY = 30;
    var FADE_OUT = 130;
    // 整塊換成內容不同的東西（例如頁籤換窗格）時改成先後淡換：兩種內容疊在一起很雜，所以舊的先淡掉，
    // 新的稍後淡入，中間只有很短一段兩個都淡淡的。比 THROUGH_HEIGHT 矮的小東西仍交叉淡化。
    var THROUGH_OUT = 90;
    var THROUGH_IN_DELAY = 50;
    var THROUGH_IN = 120;
    var THROUGH_HEIGHT = 120;
    // 移除前的晃動：最多偏離 SHAKE_REACH 像素，晃到第二下時（SHAKE_LEAD）這一次重繪的其他動態才開始，
    // 收起來接在晃動後面，下面的內容不會在晃動時就先移動。
    var SHAKE_REACH = 4;
    var SHAKE_LEAD = 130;
    var quiet = 0;
    var expectations = [];
    var removals = [];
    var followers = [];

    function keyOf(element) { return element && element.getAttribute ? element.getAttribute('data-motion-key') : null; }
    function kindOf(element) { return element.getAttribute('data-motion') || 'block'; }
    function shown(element) { return !!element && !element.closest('[hidden]'); }
    var records = {};
    // 長出來、收起來與原地改變的紀錄放在同一處：同一個東西在整段重繪與局部重繪之間都接得上。
    function registry() { return records; }

    // 清單容器的身分：最近的外層 key，加上它自己的 data-motion-list；沒有名稱時用它的標籤與第一個 class，
    // 外層多了「已完成」這類狀態 class 時仍是同一個清單。
    function listOf(container) {
      if (!container || !container.getAttribute) { return ''; }
      var owner = container.closest('[data-motion-key]');
      var name = container.getAttribute('data-motion-list');
      return (owner ? keyOf(owner) : '') + '|' + (name || ('~' + container.tagName + '.' + (container.classList[0] || '')));
    }

    // 出現的程度與此刻的速度：1 是完整出現，0 是收到看不見。被打斷時從這裡帶著速度接著走。
    function presenceAt(record, at) { return Math.min(Math.max(record.segment.at(at)[0], 0), 1); }
    function speedAt(record, at) { return record.segment.at(at)[1]; }

    // 字等空間打開一些才出現，收起時先淡掉；打開到八成五時已經完全清楚，不拖到最後。
    function opacityAt(presence) {
      var value = Math.min(Math.max((presence - 0.25) / 0.6, 0), 1);
      return value * value * (3 - 2 * value);
    }

    // 清單用 gap 隔開時，長出來或收起來的東西也帶著一段 gap。這段用負的外距跟著收放，收完拿掉時才不會跳一下：
    // 前面還有東西時算在上方（橫排是左邊），它是第一個時算在下方（右邊）。
    function gapOf(element, layout, flex, inline) {
      if (!layout || (!flex && layout.display.indexOf('grid') < 0)) { return null; }
      if (flex && (layout.flexDirection.indexOf('row') === 0) !== inline) { return null; }
      var size = parseFloat(inline ? layout.columnGap : layout.rowGap) || 0;
      if (!size) { return null; }
      var seen = false;
      var before = false;
      var after = false;
      for (var node = element.parentElement.firstElementChild; node; node = node.nextElementSibling) {
        if (node === element) { seen = true; continue; }
        if (!node.getClientRects().length || /^(absolute|fixed)$/.test(global.getComputedStyle(node).position)) { continue; }
        if (seen) { after = true; } else { before = true; }
      }
      if (!before && !after) { return null; }
      return { size: size, side: before ? (inline ? 'marginLeft' : 'marginTop') : (inline ? 'marginRight' : 'marginBottom') };
    }

    function measure(element) {
      var inline = kindOf(element) === 'inline';
      var axis = inline ? INLINE : BLOCK;
      var style = global.getComputedStyle(element);
      var rect = element.getBoundingClientRect();
      var natural = {};
      axis.forEach(function (name) {
        natural[name] = name === 'height' ? rect.height : (name === 'width' ? rect.width : (parseFloat(style[name]) || 0));
      });
      var layout = element.parentElement ? global.getComputedStyle(element.parentElement) : null;
      var flex = !!layout && layout.display.indexOf('flex') >= 0;
      return {
        axis: axis,
        natural: natural,
        inline: inline,
        gap: gapOf(element, layout, flex, inline),
        // 一排裡寫明寬度（flex-basis）的項目只改 width 不會變窄，所以一起收放。
        basis: inline && flex && style.flexBasis !== 'auto' && style.flexBasis !== 'content'
      };
    }

    function stopRecord(root, key) {
      var state = registry(root);
      var record = state[key];
      if (record && record.animation) { record.animation.cancel(); }
      delete state[key];
    }

    // 逐格取樣：大約每 16ms 一格，格與格之間交給瀏覽器補。
    function frameCount(duration) { return Math.max(Math.ceil(duration / 16), 8); }

    // 捲動容器（overflow 是 auto 或 scroll，或已經捲過的 hidden）：改它的高度或用 overflow 裁切會讓捲軸位置跑掉，
    // 所以不對它本身做原地改變，交給外層或裡面的東西。
    function scroller(element) {
      var style = global.getComputedStyle(element);
      var overflow = style.overflowX + ' ' + style.overflowY;
      return /auto|scroll/.test(overflow) || (overflow.indexOf('hidden') >= 0 && (element.scrollTop !== 0 || element.scrollLeft !== 0));
    }

    // 從 from 播到 to（0 或 1），帶著初速 options.velocity：長出來用 SPRINGS.open，收起來用 SPRINGS.close。
    // fade 只改透明度；其餘依大小逐格算好，交給瀏覽器播放。options.delay 是晚多少毫秒才開始（等移除前的晃動），
    // 這段時間停在起點；被下一次重繪打斷時，從起點接著走。
    function verticalFlexItem(element) {
      if (!element || !element.parentElement) { return false; }
      var style = global.getComputedStyle(element.parentElement);
      return (style.display === 'flex' || style.display === 'inline-flex') && style.flexDirection === 'column';
    }

    function run(root, key, element, size, from, to, options) {
      var state = registry(root);
      var lead = options.delay || 0;
      var segment = springSegment(to >= from ? SPRINGS.open : SPRINGS.close, from, to, options.velocity || 0, motionNow() + lead);
      var duration = Math.max(segment.duration, 1);
      var count = frameCount(duration);
      var frames = [];
      var freezeFlex = verticalFlexItem(element);
      for (var i = 0; i <= count; i += 1) {
        var presence = Math.min(Math.max(segment.at(segment.start + i / count * duration)[0], 0), 1);
        var frame = { offset: i / count };
        if (size) {
          frame.overflow = 'clip';
          size.axis.forEach(function (name) { frame[name] = (size.natural[name] * presence).toFixed(2) + 'px'; });
          // flex 與 grid 的項目預設不會縮得比內容小，收放期間放開最小尺寸，大小才真的跟著變。
          frame[size.inline ? 'minWidth' : 'minHeight'] = '0px';
          // 有高度上限的直向 flex 清單會把放開最小尺寸的項目一起壓扁；收放期間不伸縮，大小只照這裡算的。
          if (freezeFlex) {
            frame.flexShrink = '0';
            frame.flexGrow = '0';
          }
          if (size.basis) { frame.flexBasis = (size.natural.width * presence).toFixed(2) + 'px'; }
          if (size.gap) {
            frame[size.gap.side] = (size.natural[size.gap.side] * presence - size.gap.size * (1 - presence)).toFixed(2) + 'px';
          }
          if (size.inline) { frame.whiteSpace = 'nowrap'; }
          if (options.opacity !== false) { frame.opacity = opacityAt(presence).toFixed(3); }
        } else {
          frame.opacity = presence.toFixed(3);
        }
        frames.push(frame);
      }
      var animation = element.animate(frames, {
        duration: duration, delay: lead, easing: 'linear', fill: to === 0 ? 'both' : (lead ? 'backwards' : 'none')
      });
      var record = { element: element, segment: segment, start: segment.start, duration: duration, animation: animation };
      state[key] = record;
      element.__jetMotionRecord = record;
      animation.onfinish = function () {
        if (state[key] === record) { delete state[key]; }
        if (options.done) { options.done(); }
      };
      animation.oncancel = function () {
        if (state[key] === record) { delete state[key]; }
      };
      return animation;
    }

    // 要比對的東西：標了 data-motion-key 的，以及它們底下直接一層、沒有自己識別的區塊。後者的識別由外層的 key、
    // 標籤、第一個 class 與同名的第幾個組成，例如第三步的對照表與必填清單、第四步換掉的頁籤內容，
    // 不用另外標識別也一樣原地改變。表格、一行裡的字與浮在上面的東西不算。局部重繪時，標了識別的根也算。
    function tracked(root, withLeaving) {
      var items = [];
      var seen = {};
      var keyed = Array.prototype.slice.call(root.querySelectorAll('[data-motion-key]'));
      if (root.hasAttribute && root.hasAttribute('data-motion-key')) { keyed.unshift(root); }
      keyed.forEach(function (element) {
        if (element.parentElement && element.parentElement.closest('[data-motion-leaving]')) { return; }
        var leaving = element.hasAttribute('data-motion-leaving');
        if (leaving && !withLeaving) { return; }
        var key = keyOf(element);
        if (!key || seen[key]) { return; }
        seen[key] = true;
        items.push({ key: key, element: element, leaving: leaving, implicit: false });
        if (leaving || !shown(element)) { return; }
        var counts = {};
        var across = acrossOf(element);
        for (var child = element.firstElementChild; child; child = child.nextElementSibling) {
          if (child.hasAttribute('data-motion-key') || child.hasAttribute('data-motion-leaving') || UNSIZED.test(child.tagName)) { continue; }
          var name = child.tagName + '.' + (child.classList[0] || '');
          counts[name] = (counts[name] || 0) + 1;
          var style = global.getComputedStyle(child);
          if (style.display === 'none' || style.display === 'contents' || style.display.indexOf('inline') === 0 ||
              style.position === 'absolute' || style.position === 'fixed') { continue; }
          var implicitKey = key + '>' + name + '#' + counts[name];
          if (seen[implicitKey]) { continue; }
          seen[implicitKey] = true;
          items.push({ key: implicitKey, element: child, leaving: false, implicit: true, across: across });
        }
      });
      return items;
    }

    // 這一層的東西是不是左右排（flex 橫排或多欄的 grid）。左右排的東西出現或消失時不上下收放，
    // 在一排裡收高度看起來不對；它們只在原地改變時交叉淡化與改變高度。
    function acrossOf(element) {
      var style = global.getComputedStyle(element);
      if (style.display.indexOf('flex') >= 0) { return style.flexDirection.indexOf('row') === 0; }
      if (style.display.indexOf('grid') >= 0) { return (style.gridTemplateColumns.match(/[\d.]+px/g) || []).length > 1; }
      return false;
    }

    // 每個東西在它的容器裡和哪些要比對的東西相鄰，依畫面上的順序；也記下容器本身是哪一個要比對的東西，
    // 以及它在容器裡排第幾個，旁邊沒有可以對齊的東西時，照原本的位置放回去。
    function siblingsOf(items) {
      var keys = new global.Map();
      items.forEach(function (item) { keys.set(item.element, item.key); });
      var lists = new global.Map();
      items.forEach(function (item) {
        var parent = item.element.parentElement;
        if (!lists.has(parent)) {
          var list = [];
          for (var child = parent ? parent.firstElementChild : null; child; child = child.nextElementSibling) {
            if (keys.has(child)) { list.push(keys.get(child)); }
          }
          lists.set(parent, list);
        }
        item.siblings = lists.get(parent);
        item.parentKey = parent ? keys.get(parent) || null : null;
        item.index = parent ? Array.prototype.indexOf.call(parent.children, item.element) : -1;
      });
    }

    // 重繪前捲過的清單與表格（data-preserve-scroll），放回去的舊畫面要捲到同一個位置，淡掉或收起時才和原本看到的一樣。
    function scrollPositions(root) {
      var positions = {};
      Array.prototype.forEach.call(root.querySelectorAll('[data-preserve-scroll]'), function (element) {
        if (element.scrollLeft || element.scrollTop) {
          positions[element.getAttribute('data-preserve-scroll')] = [element.scrollLeft, element.scrollTop];
        }
      });
      return positions;
    }

    function restoreScroll(ghost, positions) {
      if (!positions) { return; }
      [ghost].concat(Array.prototype.slice.call(ghost.querySelectorAll('[data-preserve-scroll]'))).forEach(function (node) {
        var position = positions[node.getAttribute('data-preserve-scroll')];
        if (position) { node.scrollLeft = position[0]; node.scrollTop = position[1]; }
      });
    }

    // 正在輸入文字時每打一個字就重繪一次，這時字的改變直接換，不交叉淡化，免得邊打字邊閃。
    function typing(root) {
      var active = document.activeElement;
      if (!active || !root.contains(active)) { return false; }
      if (active.isContentEditable || active.tagName === 'TEXTAREA') { return true; }
      return active.tagName === 'INPUT' && !/^(?:checkbox|radio|button|submit|reset|range|color|file|image)$/i.test(active.type || '');
    }

    function capture(root) {
      if (!root || !canAnimate()) { return null; }
      var at = motionNow();
      var state = registry(root);
      var list = tracked(root, true);
      siblingsOf(list);
      var items = {};
      var elements = new global.Set();
      list.forEach(function (entry) {
        var element = entry.element;
        elements.add(element);
        var record = state[entry.key];
        var live = !!record && record.element === element && at - record.start < record.duration;
        var shift = state['change:' + entry.key];
        var shifting = !!shift && shift.element === element && at - shift.start < shift.duration;
        var parent = element.parentElement;
        var visible = shown(element);
        var box = visible ? element.getBoundingClientRect() : null;
        // 只淡入的窗格記下它在容器裡的位置，消失時在同一個位置淡掉。
        var spot = null;
        if (box && parent && kindOf(element) === 'fade') {
          var frame = parent.getBoundingClientRect();
          spot = {
            top: box.top - frame.top - parent.clientTop + parent.scrollTop,
            left: box.left - frame.left - parent.clientLeft + parent.scrollLeft
          };
        }
        items[entry.key] = {
          key: entry.key,
          element: element,
          implicit: entry.implicit,
          across: !!entry.across,
          leaving: entry.leaving,
          visible: visible,
          presence: live ? presenceAt(record, at) : 1,
          velocity: live ? speedAt(record, at) : 0,
          height: box ? box.height : 0,
          width: box ? box.width : 0,
          growth: shifting && shift.height ? shift.segment.at(at)[1] : 0,
          fading: shifting && shift.ghost,
          list: listOf(parent),
          listName: parent && parent.getAttribute ? (parent.getAttribute('data-motion-list') || '') : '',
          siblings: entry.siblings,
          parentKey: entry.parentKey,
          index: entry.index,
          spot: spot,
          owners: ownersOf(element)
        };
      });
      var menus = Array.prototype.filter.call(root.querySelectorAll(OPEN_MENUS), function (menu) {
        return !menu.closest('[data-motion-leaving]');
      }).map(function (menu) { return { id: menuId(menu), element: menu }; });
      return { items: items, elements: elements, scrolls: scrollPositions(root), typing: typing(root), menus: menus };
    }

    /* 重繪前打開的選單（「⋯」「操作」）：重繪後同一個位置是收起的新選單，舊的會一下子不見，
       例如在「⋯」裡改比對範圍或加入子條件組。所以記下它們，重繪後在原處放一份很快淡出、稍微縮回按鈕，
       和直接關閉選單時 app.css 的淡出一樣（使用者 2026-10-13 要求展開與收合一致）。 */
    var OPEN_MENUS = 'details.filter-group-menu[open], details.filter-saved-actions[open]';
    var MENUS = 'details.filter-group-menu, details.filter-saved-actions';
    var MENU_FADE = 120;

    function menuId(menu) {
      var holder = menu.parentElement ? menu.parentElement.closest('[data-motion-key]') : null;
      return (holder ? keyOf(holder) : '') + '|' + menu.className;
    }

    function fadeMenus(root, snapshot) {
      (snapshot.menus || []).forEach(function (entry) {
        if (entry.element.isConnected) { return; }
        var fresh = null;
        Array.prototype.some.call(root.querySelectorAll(MENUS), function (menu) {
          if (menu.closest('[data-motion-leaving]') || menuId(menu) !== entry.id) { return false; }
          fresh = menu;
          return true;
        });
        if (fresh && !fresh.open) { fadeMenu(entry.element, fresh); }
      });
    }

    function fadeMenu(old, fresh) {
      var parent = fresh.parentElement;
      if (!parent || global.getComputedStyle(parent).display === 'contents') { return; }
      var ghost = old.cloneNode(true);
      retire(ghost);
      ghost.setAttribute('open', '');
      var summary = ghost.querySelector('summary');
      if (summary) { summary.style.visibility = 'hidden'; }
      var panel = ghost.querySelector(':scope > :not(summary)');
      // 選單自己打開時的轉場（app.css 的 @starting-style）不重播，淡出交給下面的動畫。
      if (panel) { panel.style.transition = 'none'; }
      var look = ghost.style;
      look.position = 'absolute';
      look.margin = '0';
      look.pointerEvents = 'none';
      var position = parent.style.position;
      var anchored = global.getComputedStyle(parent).position === 'static';
      if (anchored) { parent.style.position = 'relative'; }
      function release() { if (anchored) { parent.style.position = position; } }
      if (!seat(ghost, parent, [fresh.nextSibling, null, fresh], [fresh, fresh.previousElementSibling, fresh.nextElementSibling, parent])) {
        release();
        return;
      }
      look.top = fresh.offsetTop + 'px';
      look.left = fresh.offsetLeft + 'px';
      look.width = fresh.offsetWidth + 'px';
      look.height = fresh.offsetHeight + 'px';
      var away = (panel || ghost).animate([{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'scale(0.97)' }],
        { duration: MENU_FADE, easing: 'ease-out', fill: 'forwards' });
      away.onfinish = away.oncancel = function () {
        if (ghost.parentNode) { ghost.parentNode.removeChild(ghost); }
        release();
      };
    }

    // 外層所有標了識別的東西，由內往外；用來判斷消失的東西是不是在使用者清除的範圍裡。
    function ownersOf(element) {
      var keys = [];
      for (var node = element.parentElement; node; node = node.parentElement) {
        var key = keyOf(node);
        if (key) { keys.push(key); }
      }
      return keys;
    }

    /* ---- 移除前的晃動 -------------------------------------------------------------------------------
       使用者 2026-10-13 清除條件時看到條件組晃了一下，認為「類似蘋果那樣輕微快速地晃動來作為移除元素的設計」不錯，
       要求整個條件組合、刪除案件與其他流程裡移除或清除東西時都這樣，讓使用者知道這個東西要被移除了。
       （那一下其實是清除時白紙的左右內距被撐開又收回，app.css 已經改掉；現在的晃動是刻意做的。）
       按下移除、清除或刪除的一方先用 removing 說明要拿掉的是哪些東西：keys 是那些東西自己的 data-motion-key，
       within 是外層的 data-motion-key，表示裡面消失的東西都算，only 可以再限定是哪一種（CSS 選擇器）；
       等確認或等 JetApi 回來才重繪的，用 wait 說最多等多久。接下來比對時，符合的舊元素先在原位左右晃一下，
       晃到第二下時才和這一次重繪的其他動態一起開始收起，所以看得到是哪一個要被拿掉，下面的內容也不會在晃動時就先移動。
       只晃使用者自己要拿掉的東西：切換情境、篩選清單這類讓東西消失的重繪不晃。 */
    function removing(intent) {
      if (!intent) { return; }
      var keys = [].concat(intent.keys || []).filter(Boolean);
      var within = [].concat(intent.within || []).filter(Boolean);
      if (!keys.length && !within.length) { return; }
      removals.push({ keys: keys, within: within, only: intent.only || null, until: motionNow() + (intent.wait || 1000) });
    }

    // 消失的這一個是不是使用者要拿掉的；是的話回傳那一筆說明。
    function intendedRemoval(item, intents) {
      for (var i = 0; i < intents.length; i += 1) {
        var intent = intents[i];
        var hit = intent.keys.indexOf(item.key) >= 0 ||
          (item.owners || []).some(function (key) { return intent.within.indexOf(key) >= 0; });
        if (hit && (!intent.only || (item.element.matches && item.element.matches(intent.only)))) { return intent; }
      }
      return null;
    }

    // 晃動的路徑：SPRINGS.shake 從原位帶著速度出發，來回幾次後停下；最遠偏離 SHAKE_REACH 像素，偏離小於 3% 時就停在原位。
    var shakePath = null;
    function shakeFrames() {
      if (shakePath) { return shakePath; }
      var samples = [];
      var peak = 0;
      for (var t = 0; t <= 400; t += 8) {
        var value = springState(SPRINGS.shake, 0, 1, t / 1000)[0];
        samples.push(value);
        peak = Math.max(peak, Math.abs(value));
      }
      var last = 0;
      samples.forEach(function (value, index) { if (Math.abs(value) >= peak * 0.03) { last = index; } });
      var count = last + 1;
      var frames = [];
      for (var i = 0; i <= count; i += 1) {
        var offset = i < count ? samples[i] / peak * SHAKE_REACH : 0;
        frames.push({ offset: i / count, transform: 'translateX(' + offset.toFixed(2) + 'px)' });
      }
      shakePath = { frames: frames, duration: count * 8 };
      return shakePath;
    }

    function shake(element) {
      var path = shakeFrames();
      element.animate(path.frames, { duration: path.duration, easing: 'linear' });
    }

    // 東西不會從畫面上拿掉、只是被清空時（例如清除勾選、清空搜尋），直接讓它們晃一下。
    function shakeNow(elements) {
      if (!motionAllowed() || !canAnimate() || pageHidden()) { return; }
      [].concat(elements || []).forEach(function (element) {
        if (element && element.isConnected && shown(element)) { shake(element); }
      });
    }

    // 把消失的舊元素放回新畫面裡原本的位置：先找後面還在的鄰居，再找前面的；都沒有時，放在同一個容器裡新出現的東西前面
    // （例如空白提示換成第一份報告），最後才放進同一個具名清單容器的開頭。
    // 同一個空隙裡有新出現的東西時（原地換掉，例如摘要換成編輯畫面），舊的一律放在新的前面，再由 play 讓兩個在原處交叉淡化，
    // 不會讓新的先出現在上面、把正在收起的舊的往下推，看起來像重複了一塊。
    function place(root, item, lookup, swaps, entering) {
      var ghost = item.element;
      if (ghost.isConnected) { return root.contains(ghost); }
      var index = item.siblings.indexOf(item.slot || item.key);
      var neighbor;
      for (var next = index + 1; next < item.siblings.length; next += 1) {
        neighbor = lookup(item.siblings[next]);
        if (neighbor && neighbor.parentElement && listOf(neighbor.parentElement) === item.list) {
          while (neighbor.previousElementSibling && entering(neighbor.previousElementSibling)) { neighbor = neighbor.previousElementSibling; }
          neighbor.parentElement.insertBefore(ghost, neighbor);
          return true;
        }
      }
      for (var previous = index - 1; previous >= 0; previous -= 1) {
        neighbor = lookup(item.siblings[previous]);
        if (neighbor && neighbor.parentElement && listOf(neighbor.parentElement) === item.list) {
          // 前面的鄰居自己是新出現的（例如「編輯」情境時兩個窗格交換顯示）：同一個空隙裡舊的一樣放在新的前面，
          // 由 play 讓兩個在原處交叉淡化，和按頁籤換窗格時一樣（使用者 2026-10-13 要求同一個改變用同一種動態）。
          if (entering(neighbor)) {
            while (neighbor.previousElementSibling && entering(neighbor.previousElementSibling)) { neighbor = neighbor.previousElementSibling; }
            neighbor.parentElement.insertBefore(ghost, neighbor);
          } else {
            neighbor.parentElement.insertBefore(ghost, neighbor.nextSibling);
          }
          return true;
        }
      }
      var partners = swaps[item.list];
      if (partners && partners.length && partners[0].parentElement) {
        partners[0].parentElement.insertBefore(ghost, partners[0]);
        return true;
      }
      // 旁邊沒有要比對的東西（例如一段說明裡只有這一則提示）：外層還在時，放回外層裡原本的位置。
      var holder = item.parentKey ? lookup(item.parentKey) : null;
      if (holder && holder.isConnected && !holder.closest('[data-motion-leaving]') && item.index >= 0) {
        holder.insertBefore(ghost, holder.children[item.index] || null);
        return true;
      }
      var name = item.listName;
      if (!name) { return false; }
      var selector = '[data-motion-list="' + (global.CSS && global.CSS.escape ? global.CSS.escape(name) : name) + '"]';
      var containers = root.querySelectorAll(selector);
      for (var i = 0; i < containers.length; i += 1) {
        if (listOf(containers[i]) === item.list && !containers[i].closest('[data-motion-leaving]')) {
          var first = null;
          for (var child = containers[i].firstElementChild; child; child = child.nextElementSibling) {
            if (child.hasAttribute('data-motion-key') && !child.hasAttribute('data-motion-leaving')) { first = child; break; }
          }
          containers[i].insertBefore(ghost, first);
          return true;
        }
      }
      return false;
    }

    // 放回去的舊元素只是畫面：不能按、讀屏軟體不唸，也拿掉會被程式或測試找到的識別。
    function retire(ghost) {
      if (ghost.hasAttribute('data-motion-leaving')) { return; }
      ghost.setAttribute('data-motion-leaving', '');
      ghost.setAttribute('aria-hidden', 'true');
      ghost.inert = true;
      [ghost].concat(Array.prototype.slice.call(ghost.querySelectorAll('*'))).forEach(function (node) {
        Array.prototype.slice.call(node.attributes).forEach(function (attribute) {
          var name = attribute.name;
          var own = node === ghost && (name === 'data-motion-leaving' || name === 'aria-hidden');
          if (own) { return; }
          if (DROP_ON_LEAVING.indexOf(name) >= 0 || (name.indexOf('data-') === 0 && (!KEEP_ON_LEAVING.test(name) ||
              (node !== ghost && name === 'data-motion-key')))) {
            node.removeAttribute(name);
          }
        });
      });
    }

    function drop(root, key, ghost) {
      stopRecord(root, key);
      if (ghost && ghost.parentNode) { ghost.parentNode.removeChild(ghost); }
    }

    // element 的外層（到 stop 為止）有沒有在 set 裡：外層也在長出來或收起來時，只動最外層的那一個。
    function insideAny(element, set, stop) {
      for (var node = element.parentElement; node && node !== stop; node = node.parentElement) {
        if (set.has(node)) { return true; }
      }
      return false;
    }

    // element 裡面有沒有 set 裡的其他東西。
    function holdsAny(element, set) {
      var found = false;
      set.forEach(function (other) { if (!found && other !== element && element.contains(other)) { found = true; } });
      return found;
    }

    // 預約：加入的條件會由這裡長出來時，交給呼叫的一方決定何時顯示內容（例如飛過去的標籤到了才出現）。
    function claim(candidates) {
      if (!expectations.length) { return null; }
      var expectation = expectations[0];
      var chosen = expectation.choose(candidates.map(function (item) { return item.element; }));
      if (!chosen) { return null; }
      expectations.shift();
      for (var i = 0; i < candidates.length; i += 1) {
        if (candidates[i].element === chosen) { return { item: candidates[i], expectation: expectation }; }
      }
      return null;
    }

    // 視窗縮到最小或被蓋住時不播：畫面看不到，播了也只會在回來時一起補播。
    function pageHidden() { return document.visibilityState === 'hidden'; }

    // 數列裡最長的一段遞增子序列；回傳每個位置是否在裡面。
    function longestRising(values) {
      var size = values.map(function () { return 1; });
      var previous = values.map(function () { return -1; });
      var best = -1;
      for (var i = 0; i < values.length; i += 1) {
        for (var j = 0; j < i; j += 1) {
          if (values[j] < values[i] && size[j] + 1 > size[i]) { size[i] = size[j] + 1; previous[i] = j; }
        }
        if (best < 0 || size[i] > size[best]) { best = i; }
      }
      var keep = values.map(function () { return false; });
      for (var k = best; k >= 0; k = previous[k]) { keep[k] = true; }
      return keep;
    }

    // 同一個清單裡換了順序的東西（例如設為主要條件）：順序沒變的最長一段留在原地，
    // 其餘的在原來的位置收起來、在新的位置長出來，中間的東西跟著平順移動，字不會疊在一起滑過去。只看標了識別的東西。
    function reordered(snapshot, current) {
      var moved = {};
      var parents = new global.Map();
      Object.keys(snapshot.items).forEach(function (key) {
        var before = snapshot.items[key];
        var element = current[key];
        if (before.implicit || before.leaving || !before.visible || !element || !shown(element) || !element.parentElement) { return; }
        // 只處理重繪重建出來的新元素；同一個元素被搬動時舊的那一份就是它自己，不能拿來收起。
        if (element === before.element || before.element.isConnected) { return; }
        if (listOf(element.parentElement) !== before.list) { return; }
        var keys = parents.get(element.parentElement);
        if (!keys) { keys = []; parents.set(element.parentElement, keys); }
        keys.push(key);
      });
      parents.forEach(function (keys, parent) {
        if (keys.length < 2) { return; }
        var order = [];
        for (var child = parent.firstElementChild; child; child = child.nextElementSibling) {
          var key = keyOf(child);
          if (key && current[key] === child && keys.indexOf(key) >= 0) { order.push(key); }
        }
        var keep = longestRising(order.map(function (key) { return snapshot.items[key].siblings.indexOf(key); }));
        order.forEach(function (key, i) { if (!keep[i]) { moved[key] = true; } });
      });
      return moved;
    }

    // 自己這一層的字，不含底下另外比對的東西（那些各自處理）。
    function ownText(element, others) {
      var text = '';
      (function walk(node) {
        for (var child = node.firstChild; child; child = child.nextSibling) {
          if (child.nodeType === 3) { text += child.nodeValue; } else if (child.nodeType === 1 && !others.has(child)) { walk(child); }
        }
      })(element);
      return text;
    }

    // 交叉淡化期間暫時讓容器成為定位的依據與獨立的一層：舊的那一份才疊得到新的那一份的位置上，
    // 並且一直畫在新的底下、不蓋到容器外面的東西。這個容器裡的交叉淡化全部播完才還原。
    function holdPosition(parent) {
      var hold = parent.__jetMotionHold;
      if (!hold) {
        hold = parent.__jetMotionHold = { count: 0, position: parent.style.position, isolation: parent.style.isolation };
        if (global.getComputedStyle(parent).position === 'static') { parent.style.position = 'relative'; }
        parent.style.isolation = 'isolate';
      }
      hold.count += 1;
    }

    function releasePosition(parent) {
      var hold = parent.__jetMotionHold;
      if (!hold) { return; }
      hold.count -= 1;
      if (hold.count <= 0) {
        parent.style.position = hold.position;
        parent.style.isolation = hold.isolation;
        parent.__jetMotionHold = null;
      }
    }

    // 舊的那一份每一格都對齊新的那一份：上面有東西正在收起或長出來時，兩份一起移動，不會錯開。
    // 新的那一份被之後的重繪換掉時，舊的那一份也一起拿掉，不留在畫面上。
    function follow(ghost, element, life) {
      followers.push({ ghost: ghost, element: element, life: life });
      if (followers.length === 1) { global.requestAnimationFrame(track); }
    }

    function track() {
      followers = followers.filter(function (entry) {
        if (entry.life.ended || !entry.ghost.isConnected) { return false; }
        if (!entry.element.isConnected) { entry.life.end(false); return false; }
        entry.ghost.style.top = entry.element.offsetTop + 'px';
        entry.ghost.style.left = entry.element.offsetLeft + 'px';
        return true;
      });
      if (followers.length) { global.requestAnimationFrame(track); }
    }

    // 高度的彈簧逐格取樣；opacity(time) 有給時一併寫上透明度。
    function heightFrames(segment, duration, opacity, element) {
      var frames = [];
      var count = frameCount(duration);
      var freezeFlex = verticalFlexItem(element);
      for (var i = 0; i <= count; i += 1) {
        var time = i / count * duration;
        // 改高度期間不伸縮，理由同 run()。
        var frame = {
          offset: i / count,
          height: Math.max(segment.at(segment.start + time)[0], 0).toFixed(2) + 'px',
          minHeight: '0px',
          overflow: 'clip'
        };
        if (freezeFlex) { frame.flexShrink = '0'; frame.flexGrow = '0'; }
        if (opacity) { frame.opacity = opacity(time).toFixed(3); }
        frames.push(frame);
      }
      return frames;
    }

    // 只改透明度的逐格取樣：高度不變的交叉淡化與捲動容器用，不裁切，捲軸位置不會跑掉。
    function opacityFrames(duration, opacity) {
      var frames = [];
      var count = frameCount(duration);
      for (var i = 0; i <= count; i += 1) { frames.push({ offset: i / count, opacity: opacity(i / count * duration).toFixed(3) }); }
      return frames;
    }

    // 把舊的那一份放進容器裡，但不改變新的畫面。有些樣式看前後是誰（例如預篩選說明後面的按鈕列多留一點空間），
    // 舊的那一份插在新的前面或後面，都可能讓這類樣式失效、新的畫面跟著跳一下。所以依序試 spots 裡的位置，
    // 選一個 around（新的那一份、它前後的東西與容器）都不受影響的位置；都不行就不放，直接換。舊的那一份要先設成不佔位置。
    function seat(ghost, parent, spots, around) {
      function shape() {
        return around.map(function (node) {
          if (!node) { return ''; }
          var box = node.getBoundingClientRect();
          return [box.top, box.left, box.width, box.height].map(function (value) { return value.toFixed(1); }).join(',');
        }).join('|');
      }
      if (ghost.parentNode) { ghost.parentNode.removeChild(ghost); }
      var expected = shape();
      for (var i = 0; i < spots.length; i += 1) {
        parent.insertBefore(ghost, spots[i]);
        if (shape() === expected) { return true; }
      }
      parent.removeChild(ghost);
      return false;
    }

    // 捲過的清單與表格先記下要捲回哪裡（retire 會拿掉 data-preserve-scroll），放好之後再捲。
    function scrollPlan(ghost, positions) {
      var plan = [];
      if (positions) {
        [ghost].concat(Array.prototype.slice.call(ghost.querySelectorAll('[data-preserve-scroll]'))).forEach(function (node) {
          var position = positions[node.getAttribute('data-preserve-scroll')];
          if (position) { plan.push([node, position[0], position[1]]); }
        });
      }
      return function () { plan.forEach(function (entry) { entry[0].scrollLeft = entry[1]; entry[0].scrollTop = entry[2]; }); };
    }

    function fadeIn(time) { return Math.min(time / FADE_IN, 1); }
    function fadeOut(time) { return Math.min(Math.max(1 - (time - FADE_OUT_DELAY) / FADE_OUT, 0), 1); }
    function throughIn(time) { return Math.min(Math.max((time - THROUGH_IN_DELAY) / THROUGH_IN, 0), 1); }
    function throughOut(time) { return Math.min(Math.max(1 - time / THROUGH_OUT, 0), 1); }

    function depthOf(element) {
      var depth = 0;
      for (var node = element; node; node = node.parentElement) { depth += 1; }
      return depth;
    }

    // 原地改變（使用者 2026-10-12 要求切換時流暢、不要一閃而過）：
    // - options.height 為 true 時，element 的高度沿彈簧從 options.from 變成 options.to，下面的內容跟著平順移動；
    // - 有 options.ghost（舊的那一份）時，舊的疊在 element 底下晚一點淡掉，element 很快淡入，也就是原地交叉淡化；
    //   options.through 為 true 時改成先後淡換（見 THROUGH_OUT）。改高度時兩份跟著同樣的高度。
    //   options.scroll(ghost) 回傳一個函式，舊的那一份放好之後用它捲回原本看到的位置。
    // options.delay 同 run()：等移除前的晃動，這段時間維持原本的樣子。
    // 紀錄放在 'change:' + key，下一次重繪時從此刻的高度與速度接著走。
    function change(root, key, element, options) {
      var to = typeof options.to === 'number' ? options.to : element.getBoundingClientRect().height;
      var preset = to >= options.from ? SPRINGS.open : SPRINGS.close;
      var lead = options.delay || 0;
      var segment = springSegment(preset, options.from, to, options.height ? options.velocity || 0 : 0, motionNow() + lead);
      var ghost = options.ghost || null;
      var parent = element.parentElement;
      if (ghost && ghost.__jetMotionLife) {
        // 同一個畫面週期裡又重繪一次時，由這一次接手：停掉上一次的淡出，但先不拿掉它。
        ghost.__jetMotionLife.end(true);
      }
      if (ghost && (!parent || global.getComputedStyle(parent).display === 'contents')) {
        if (ghost.parentNode) { ghost.parentNode.removeChild(ghost); }
        ghost = null;
      }
      if (ghost) {
        var restore = options.scroll ? options.scroll(ghost) : null;
        retire(ghost);
        ghost.removeAttribute('data-motion-key');
        var look = ghost.style;
        look.position = 'absolute';
        look.zIndex = '-1';
        look.margin = '0';
        look.width = options.width + 'px';
        look.pointerEvents = 'none';
        look.top = '0px';
        look.left = '0px';
        holdPosition(parent);
        // 先拿出來再看新的那一份前後是誰：交叉淡化的一對裡，舊的那一份原本就放在新的前面。
        if (ghost.parentNode) { ghost.parentNode.removeChild(ghost); }
        if (seat(ghost, parent, [element, element.nextSibling, null],
            [element, element.previousElementSibling, element.nextElementSibling, parent])) {
          look.top = element.offsetTop + 'px';
          look.left = element.offsetLeft + 'px';
          if (restore) { restore(); }
        } else {
          releasePosition(parent);
          ghost = null;
        }
      }
      var through = !!options.through;
      var outgoing = through ? throughOut : fadeOut;
      var incoming = through ? throughIn : fadeIn;
      var duration = Math.max(options.height ? segment.duration : 0,
        ghost ? (through ? THROUGH_IN_DELAY + THROUGH_IN : FADE_OUT_DELAY + FADE_OUT) : 0, 1);
      if (ghost) {
        var life = { parent: parent, ended: false, fade: null };
        life.end = function (keep) {
          if (life.ended) { return; }
          life.ended = true;
          if (life.fade) {
            life.fade.onfinish = null;
            life.fade.oncancel = null;
            if (keep) { life.fade.cancel(); }
          }
          if (!keep && ghost.parentNode) { ghost.parentNode.removeChild(ghost); }
          releasePosition(life.parent);
          if (ghost.__jetMotionLife === life) { ghost.__jetMotionLife = null; }
        };
        ghost.__jetMotionLife = life;
        life.fade = ghost.animate(options.height ? heightFrames(segment, duration, outgoing, ghost) : opacityFrames(duration, outgoing),
          { duration: duration, delay: lead, easing: 'linear', fill: 'both' });
        life.fade.onfinish = function () { life.end(false); };
        life.fade.oncancel = function () { life.end(false); };
        follow(ghost, element, life);
      }
      var frames = options.height ? heightFrames(segment, duration, ghost ? incoming : null, element) : opacityFrames(duration, incoming);
      var animation = element.animate(frames, { duration: duration, delay: lead, easing: 'linear', fill: lead ? 'backwards' : 'none' });
      var state = registry(root);
      var record = {
        element: element, segment: segment, start: segment.start, duration: duration,
        height: !!options.height, ghost: !!ghost, animation: animation
      };
      state['change:' + key] = record;
      animation.onfinish = animation.oncancel = function () {
        if (state['change:' + key] === record) { delete state['change:' + key]; }
      };
    }

    function play(root, snapshot) {
      if (!root || !snapshot) { return; }
      var currentList = tracked(root, false);
      var current = {};
      var implicit = {};
      var across = {};
      var currentElements = new global.Set();
      currentList.forEach(function (item) {
        current[item.key] = item.element;
        currentElements.add(item.element);
        if (item.implicit) { implicit[item.key] = true; }
        if (item.across) { across[item.key] = true; }
      });
      var allowed = quiet === 0 && motionAllowed() && canAnimate() && !pageHidden();
      var moved = allowed ? reordered(snapshot, current) : {};
      var entering = [];
      var leaving = [];
      var staying = [];
      Object.keys(snapshot.items).forEach(function (key) {
        var before = snapshot.items[key];
        var element = current[key];
        var back = !!element && shown(element);
        if (before.leaving && (back || !allowed)) { drop(root, key, before.element); return; }
        if (moved[key]) {
          leaving.push({ key: key + MOVED, slot: key, element: before.element, presence: before.presence, velocity: before.velocity,
            height: before.height, width: before.width, list: before.list, listName: before.listName, siblings: before.siblings,
            parentKey: before.parentKey, index: before.index });
          return;
        }
        if (back) {
          if (before.visible && !before.leaving && before.presence >= 0.999) { staying.push({ key: key, element: element, before: before }); }
          return;
        }
        if (!before.visible || before.across) { return; }
        // 舊元素還在頁面上、只是被藏起來（例如矩陣第一次載入時藏起沒選的檢視，藏起來後追蹤的編號也會跟著變）：
        // 收起來的要是它的複本。下面放回舊元素時會拿掉識別，用元素本身會讓還在用的元素再也找不到
        // （2026-10-14 矩陣切不到傳票交叉表）。
        if (!before.leaving && before.element.isConnected) {
          leaving.push(Object.assign({}, before, { element: before.element.cloneNode(true) }));
          return;
        }
        leaving.push(before);
      });
      Object.keys(current).forEach(function (key) {
        var element = current[key];
        if (!shown(element)) { return; }
        var before = snapshot.items[key];
        var known = !!before && before.visible && !moved[key];
        var presence = known ? before.presence : 0;
        if (presence < 0.999 && !across[key]) {
          entering.push({ key: key, element: element, presence: presence, velocity: known ? before.velocity : 0, implicit: !!implicit[key] });
        }
      });
      if (!allowed) {
        entering.forEach(function (item) { stopRecord(root, item.key); });
        expectations = [];
        // 不需要動態的重繪（still）之後，同一個畫面週期裡的下一次重繪拿這次的結果來比，
        // 例如換成正式編號的身分不會在下一次重繪被當成移除再加入。
        if (quiet > 0) {
          root.__jetMotionPending = false;
          root.__jetMotionSnapshot = null;
        }
        return;
      }

      var enteringSet = new global.Set(entering.map(function (item) { return item.element; }));
      entering = entering.filter(function (item) { return !insideAny(item.element, enteringSet, root); });
      var leavingSet = new global.Set(leaving.map(function (item) { return item.element; }));
      var nested = leaving;
      leaving = leaving.filter(function (item) { return !insideAny(item.element, leavingSet, null); });
      // 記下一起消失的裡面那些：使用者拿掉的是最後一個小標籤、整行跟著消失時，晃的是那一整行（見上面的 removing）。
      leaving.forEach(function (item) {
        item.inner = nested.filter(function (other) { return other !== item && item.element.contains(other.element); });
      });

      // 先量新畫面的大小與預約的落點，這時還沒放回消失的舊元素，量到的就是播完之後的位置。
      var claimed = claim(entering.filter(function (item) { return !item.implicit && kindOf(item.element) !== 'fade'; }));
      entering.forEach(function (item) {
        item.size = kindOf(item.element) === 'fade' ? null : measure(item.element);
      });
      if (claimed) { claimed.data = claimed.expectation.measure(claimed.item.element); }

      // 原地改變的候選：同一個東西重繪後高度變了，或重繪重建後自己的字變了。同樣在放回舊元素之前量與比對；
      // 舊的那一份裡面有要收起的東西時，那些東西會被搬去新的畫面，舊的那一份不再完整，所以不拿來交叉淡化。
      // 捲動容器只交叉淡化、不改高度，高度交給外層。
      var limit = global.innerHeight || 800;
      var changing = staying.filter(function (item) {
        var element = item.element;
        if (kindOf(element) !== 'block' || UNSIZED.test(element.tagName) || insideAny(element, enteringSet, root)) { return false; }
        var old = item.before.element;
        item.scrolls = scroller(element);
        item.to = element.getBoundingClientRect().height;
        item.grow = !item.scrolls && item.before.height > 0 && Math.abs(item.to - item.before.height) > 2;
        item.fade = old !== element && !old.isConnected && !snapshot.typing && !item.before.fading &&
          Math.max(item.to, item.before.height) <= limit &&
          !leaving.some(function (other) { return old.contains(other.element); }) &&
          ownText(old, snapshot.elements) !== ownText(element, currentElements);
        // 交叉淡化用舊元素的複本，理由同下面放回去的舊元素。
        if (item.fade) { item.copy = old.cloneNode(true); }
        return true;
      });

      var placed = {};
      var swaps = {};
      var arriving = new global.Map();
      entering.forEach(function (item) {
        var list = listOf(item.element.parentElement);
        (swaps[list] = swaps[list] || []).push(item.element);
        arriving.set(item.element, item);
      });
      // 換了順序的東西拿它舊的位置當鄰居；新的位置不算，其他舊元素才放得回原本的順序。
      function lookup(key) { return moved[key] ? (placed[key + MOVED] || null) : (current[key] || placed[key] || null); }
      function isEntering(element) { return arriving.has(element); }
      // 放回去的是舊元素的複本：重繪前的畫面原封不動，同一個畫面週期裡再重繪一次時（例如切換金額欄位格式時先清掉
      // 不適用的欄位、再換格式），拿來比對的仍是完整的舊畫面，不會少了被上一次搬走的部分。
      var fadingOut = [];
      leaving = leaving.map(function (item) {
        if (item.leaving || item.element.isConnected) { return item; }
        return Object.assign({}, item, { element: item.element.cloneNode(true) });
      }).filter(function (item) {
        var faded = kindOf(item.element) === 'fade';
        if ((faded && (item.leaving || !item.spot)) || !place(root, item, lookup, swaps, isEntering)) {
          stopRecord(root, item.key);
          if (item.element.parentNode && (item.leaving || faded)) { item.element.parentNode.removeChild(item.element); }
          return false;
        }
        stopRecord(root, item.key);
        restoreScroll(item.element, snapshot.scrolls);
        retire(item.element);
        if (faded) { fadingOut.push(item); return false; }
        if (item.slot || item.implicit) { item.element.setAttribute('data-motion-key', item.key); }
        placed[item.key] = item.element;
        return true;
      });

      // 使用者要拿掉的東西（見上面的 removing）先晃一下：它們不和新出現的東西原地交叉淡化，晃完自己收起來；
      // 這一次重繪的其他動態晚 SHAKE_LEAD 才開始。已經在收起中、或只是換了順序的不晃。
      var doomed = new global.Set();
      var now = motionNow();
      removals = removals.filter(function (intent) { return intent.until > now; });
      if (removals.length) {
        // 只淡出的窗格（data-motion="fade"）也算，例如第三步取消勾選的攸關資料元素欄位。
        leaving.concat(fadingOut).forEach(function (item) {
          if (item.slot || item.leaving || item.presence < 0.999) { return; }
          var intent = intendedRemoval(item, removals);
          for (var i = 0; !intent && item.inner && i < item.inner.length; i += 1) { intent = intendedRemoval(item.inner[i], removals); }
          if (intent) { doomed.add(item); intent.used = true; }
        });
        removals = removals.filter(function (intent) { return !intent.used; });
      }
      var lead = doomed.size ? SHAKE_LEAD : 0;

      // 原地換掉：同一個空隙裡一串舊的收起、一串新的長出（例如摘要換成編輯畫面、借方與貸方兩欄換成一個金額欄），
      // 依順序一對一在原處交叉淡化，新的從舊的高度變成自己的高度；多出來的舊的移到最後一個新的後面再收起，
      // 多出來的新的照常長出來（Apple 讓東西在兩個狀態之間變形，而不是消失再出現）。
      var pairs = [];
      var crossing = new global.Set();
      var pairedLeaving = new global.Set();
      var ghosts = new global.Map();
      var parents = new global.Set();
      leaving.forEach(function (item) {
        ghosts.set(item.element, item);
        if (item.element.parentElement) { parents.add(item.element.parentElement); }
      });
      parents.forEach(function (parent) {
        var olds = [];
        var news = [];
        function flush() {
          var count = Math.min(olds.length, news.length);
          for (var i = 0; i < count; i += 1) {
            crossing.add(news[i]);
            pairedLeaving.add(olds[i]);
            pairs.push({ item: news[i], ghost: olds[i].element, from: olds[i].height, width: olds[i].width });
          }
          var anchor = count ? news[count - 1].element : null;
          olds.slice(count).forEach(function (item) {
            if (anchor) { anchor.parentNode.insertBefore(item.element, anchor.nextSibling); anchor = item.element; }
          });
          olds = [];
          news = [];
        }
        for (var child = parent.firstElementChild; child;) {
          var next = child.nextElementSibling;
          var old = ghosts.get(child);
          var fresh = arriving.get(child);
          if (old) {
            if (news.length) { flush(); }
            if (!old.slot && old.presence >= 0.5 && kindOf(child) === 'block' && !doomed.has(old)) { olds.push(old); }
          } else if (fresh) {
            if (olds.length && fresh.presence <= 0.001 && !(claimed && claimed.item === fresh) && fresh.size && !fresh.size.inline) {
              news.push(fresh);
            } else {
              flush();
            }
          } else if (!child.hasAttribute('data-motion-leaving')) {
            flush();
          }
          child = next;
        }
        flush();
      });
      leaving = leaving.filter(function (item) { return !pairedLeaving.has(item); });
      entering = entering.filter(function (item) { return !crossing.has(item); });

      // 裡面還有東西在長出來、收起來或改變高度時，外層的高度交給它們自然帶動，外層不另外改高度；
      // 裡面有任何東西在動時，外層也不交叉淡化，只換它自己的字。只淡入的窗格不改大小，不算。
      var sizing = new global.Set();
      entering.forEach(function (item) { if (item.size) { sizing.add(item.element); } });
      leaving.forEach(function (item) { sizing.add(item.element); });
      pairs.forEach(function (pair) { sizing.add(pair.item.element); });
      changing.forEach(function (item) { if (item.grow) { sizing.add(item.element); } });
      var fading = new global.Set();
      changing.forEach(function (item) { if (item.fade) { fading.add(item.element); } });
      changing.forEach(function (item) {
        if (item.grow && holdsAny(item.element, sizing)) { item.grow = false; }
        if (item.fade && (holdsAny(item.element, sizing) || holdsAny(item.element, fading))) { item.fade = false; }
      });

      function restore(ghost) { return scrollPlan(ghost, snapshot.scrolls); }
      function start(item, height) {
        item.started = true;
        stopRecord(root, 'change:' + item.key);
        change(root, item.key, item.element, {
          from: item.before.height, to: item.to, height: height, velocity: item.before.growth,
          ghost: item.fade ? item.copy : null, width: item.before.width, scroll: restore, delay: lead
        });
      }

      // 先播會改變高度的：交叉淡化的一對、長出來的、收起來的，以及最內層原地改變高度的。
      pairs.forEach(function (pair) {
        stopRecord(root, pair.item.key);
        stopRecord(root, 'change:' + pair.item.key);
        change(root, pair.item.key, pair.item.element, {
          from: pair.from, to: pair.item.size.natural.height, height: true, ghost: pair.ghost, width: pair.width,
          through: Math.max(pair.from, pair.item.size.natural.height) > THROUGH_HEIGHT, delay: lead
        });
      });
      leaving.forEach(function (item) { item.size = measure(item.element); });
      doomed.forEach(function (item) { shake(item.element); });
      entering.forEach(function (item) {
        stopRecord(root, item.key);
        run(root, item.key, item.element, item.size, item.presence, 1, { velocity: item.velocity, delay: lead });
      });
      leaving.forEach(function (item) {
        var ghost = item.element;
        run(root, item.key, ghost, item.size, item.presence, 0, {
          velocity: item.velocity,
          delay: lead,
          done: function () { if (ghost.parentNode) { ghost.parentNode.removeChild(ghost); } }
        });
      });
      changing.forEach(function (item) { if (item.grow) { start(item, true); } });

      // 外層的高度原本交給裡面的動態帶動；裡面沒有接住的改變（例如對照表換了列數，表格不能收放）仍會讓它一下變高或變矮。
      // 這時外層自己沿彈簧從原本的高度變成新的高度。由內往外檢查，裡面補上之後，外層量到的就已經是對的。
      changing.filter(function (item) {
        return !item.started && !item.scrolls && item.before.height > 0 && Math.abs(item.to - item.before.height) > 2;
      }).sort(function (a, b) { return depthOf(b.element) - depthOf(a.element); }).forEach(function (item) {
        if (Math.abs(item.element.getBoundingClientRect().height - item.before.height) > 2) { start(item, true); }
      });

      // 最後是只換字的：原地交叉淡化，高度不變。
      changing.forEach(function (item) { if (item.fade && !item.started) { start(item, false); } });

      // 只淡入的窗格（data-motion="fade"）消失時，在原來的位置淡掉，不再一下子不見；它不佔位置，旁邊的東西照常移動。
      fadingOut.forEach(function (item) {
        var ghost = item.element;
        var parent = ghost.parentElement;
        if (!parent || global.getComputedStyle(parent).display === 'contents') {
          if (parent) { parent.removeChild(ghost); }
          return;
        }
        ghost.removeAttribute('data-motion-key');
        var look = ghost.style;
        look.position = 'absolute';
        look.zIndex = '-1';
        look.margin = '0';
        look.top = item.spot.top + 'px';
        look.left = item.spot.left + 'px';
        look.width = item.width + 'px';
        look.height = item.height + 'px';
        look.pointerEvents = 'none';
        holdPosition(parent);
        if (!seat(ghost, parent, [ghost.nextSibling, null], [ghost.previousElementSibling, ghost.nextElementSibling, parent])) {
          releasePosition(parent);
          return;
        }
        var away = ghost.animate(opacityFrames(FADE_OUT, function (time) { return 1 - time / FADE_OUT; }),
          { duration: FADE_OUT, delay: lead, easing: 'linear', fill: 'both' });
        away.onfinish = away.oncancel = function () {
          if (ghost.parentNode) { ghost.parentNode.removeChild(ghost); }
          releasePosition(parent);
        };
      });
      fadeMenus(root, snapshot);
      if (claimed) { claimed.expectation.start(claimed.item.element, claimed.data); }
    }

    // 重繪前記下畫面。同一個畫面週期裡連續重繪時（例如開啟案件時一次十幾次、重繪途中又觸發重繪），
    // 中間的版本從沒畫到螢幕上，所以一律拿這個週期第一次記下的、使用者真的看過的畫面來比；
    // 這個週期裡只要有一次是整塊換掉（allowed 為 false），之後的重繪也都不比對。
    // 視窗縮到最小時瀏覽器不排下一個畫面週期，記下的畫面超過一秒就不再沿用，免得之後的重繪一直拿很久以前的畫面比。
    function begin(root, allowed) {
      if (!root) { return null; }
      var at = motionNow();
      if (root.__jetMotionPending && at - root.__jetMotionAt < 1000) {
        if (!allowed) { root.__jetMotionSnapshot = null; }
        return root.__jetMotionSnapshot;
      }
      root.__jetMotionSnapshot = allowed ? capture(root) : null;
      root.__jetMotionPending = true;
      root.__jetMotionAt = at;
      if (typeof global.requestAnimationFrame === 'function') {
        global.requestAnimationFrame(function () {
          root.__jetMotionPending = false;
          root.__jetMotionSnapshot = null;
        });
      } else {
        root.__jetMotionPending = false;
      }
      return root.__jetMotionSnapshot;
    }

    // 局部重繪也走同一套：先記下，再重繪，再比對。重繪期間原地展開區不重播長出來的動畫。
    function morph(root, render) {
      var snapshot = begin(root, true);
      if (root) { root.setAttribute('data-motion-rendering', ''); }
      try {
        render();
        play(root, snapshot);
      } finally {
        if (root) {
          void root.offsetHeight;
          root.removeAttribute('data-motion-rendering');
        }
      }
    }

    // 不需要動態的重繪（例如身分整批換掉的儲存結果）：照常比對，但直接換。
    function still(render) {
      quiet += 1;
      try { render(); } finally { quiet -= 1; }
    }

    // 單一個新加入的元素長出來，用在不經過整段重繪的清單（例如訊息），以及展開目前的步驟時整段內容從標題長出來。
    // from 是 progress() 記下的進度：長到一半整段重繪時，新的那一份從同樣的大小與速度接著長。
    function enter(element, from) {
      if (!element || !motionAllowed() || !canAnimate() || pageHidden() || !shown(element)) { return; }
      var holder = element.parentElement || element;
      var start = from && from.to === 1 ? from : { presence: 0, velocity: 0 };
      run(holder, '__enter' + (++enter.count), element, measure(element), start.presence, 1, { velocity: start.velocity });
    }
    enter.count = 0;

    // 單一個元素此刻長出來或收起來到哪裡、速度多少；沒有在播時回傳 null。
    function progress(element) {
      var record = element && element.__jetMotionRecord;
      if (!record || record.element !== element) { return null; }
      var at = motionNow();
      if (at - record.start >= record.duration) { return null; }
      return { presence: presenceAt(record, at), velocity: speedAt(record, at), to: record.segment.to };
    }

    // 已經從畫面上拿掉的舊元素放進 holder 的最後一格（整列寬），收起來再拿掉；用在換步驟時原本那一步的內容收回它的那一列。
    // from 是它被拿掉時 progress() 記下的進度（例如還在長出來就換步驟），從那裡接著收。
    // 放回去的舊元素和其他收起中的舊元素一樣不能按、讀屏軟體不唸。不能播動態時不放回去，回傳 false。
    function leave(element, holder, from) {
      if (!element || !holder || !motionAllowed() || !canAnimate() || pageHidden()) { return false; }
      if (element.__jetMotionRecord) { element.__jetMotionRecord.animation.cancel(); }
      retire(element);
      element.style.flex = '0 0 100%';
      // 排在那一列所有東西之後：列裡的展開箭頭用 order 排到最右邊，收回的內容排在它前面時，箭頭會被擠到下一行，
      // 收完拿掉的那一格，下面各列一次往上跳（使用者 2026-10-15 錄影指出切換尾段的抖動）。
      var last = 0;
      Array.prototype.forEach.call(holder.children, function (child) {
        if (child !== element) { last = Math.max(last, parseFloat(global.getComputedStyle(child).order) || 0); }
      });
      element.style.order = String(last + 1);
      holder.appendChild(element);
      var start = from ? from : { presence: 1, velocity: 0 };
      run(holder, '__leave' + (++leave.count), element, measure(element), start.presence, 0, {
        velocity: start.velocity,
        done: function () { if (element.parentNode) { element.parentNode.removeChild(element); } }
      });
      return true;
    }
    leave.count = 0;

    // 先把一整塊收回去，收完才交給 done 換畫面，用在收合目前的步驟：內容收回標題，再換成一行摘要。
    // 收的期間不能按；再按一次開關不會重來。不能播動態時直接 done。
    function fold(element, done) {
      if (!element || !element.isConnected || !motionAllowed() || !canAnimate() || pageHidden()) { done(); return; }
      if (element.hasAttribute('data-motion-folding')) { return; }
      element.setAttribute('data-motion-folding', '');
      element.inert = true;
      run(element.parentElement || element, '__fold' + (++fold.count), element, measure(element), 1, 0, { done: done });
    }
    fold.count = 0;

    // 頁籤底下原地切換的窗格：窗格自己淡入，裡面原本就打開的展開區保持原樣，不因為窗格重新出現而再長一次。
    function setShown(element, visible) {
      if (!element) { return; }
      if (!visible) { element.hidden = true; return; }
      if (!element.hidden) { return; }
      element.setAttribute('data-motion-revealing', '');
      element.hidden = false;
      void element.offsetHeight;
      element.removeAttribute('data-motion-revealing');
    }

    // 捲過的地方（含自己），依在 element 裡的順序記下；複製出來的舊窗格捲到同一個位置。
    function scrolledWithin(element) {
      var found = [];
      if (element.scrollTop || element.scrollLeft) { found.push({ index: -1, left: element.scrollLeft, top: element.scrollTop }); }
      var all = element.getElementsByTagName('*');
      for (var i = 0; i < all.length; i += 1) {
        if (all[i].scrollTop || all[i].scrollLeft) { found.push({ index: i, left: all[i].scrollLeft, top: all[i].scrollTop }); }
      }
      return found;
    }

    // 頁籤底下原地換窗格（使用者 2026-10-12）：新的窗格從舊窗格的高度平順變成自己的高度，
    // 舊的那一份疊在底下先淡掉，新的接著淡入，兩份內容不會同時疊在一起；下面的內容跟著平順移動。
    // fill 在新的窗格顯示之後、量高度之前補上內容。
    // 兩個窗格要在同一個容器裡；不能播動態時直接換。
    function swap(from, to, fill) {
      if (!to) { return; }
      var smooth = !!from && from !== to && !from.hidden && from.isConnected && from.getClientRects().length > 0 &&
        from.parentElement === to.parentElement && motionAllowed() && canAnimate() && !pageHidden();
      if (!smooth) {
        if (from && from !== to) { from.hidden = true; }
        setShown(to, true);
        if (fill) { fill(); }
        return;
      }
      var box = from.getBoundingClientRect();
      var ghost = from.cloneNode(true);
      var scrolled = scrolledWithin(from);
      from.hidden = true;
      // 窗格自己的淡入（app.css 的 [data-motion-pane]）交給這裡的交叉淡化，不重複播。
      var transition = to.style.transition;
      to.style.transition = 'none';
      setShown(to, true);
      if (fill) { fill(); }
      void to.offsetHeight;
      to.style.transition = transition;
      change(to.parentElement, 'swap:' + (++swap.count), to, {
        from: box.height, height: true, ghost: ghost, width: box.width, through: true,
        scroll: function (copy) {
          var all = copy.getElementsByTagName('*');
          return function () {
            scrolled.forEach(function (entry) {
              var node = entry.index < 0 ? copy : all[entry.index];
              if (node) { node.scrollLeft = entry.left; node.scrollTop = entry.top; }
            });
          };
        }
      });
    }
    swap.count = 0;

    // 先淡出、縮回一點再拿掉，用在直接從畫面移除的對話框。
    function dismiss(element, done) {
      if (!element || !motionAllowed() || !canAnimate() || !element.isConnected) {
        if (element && element.parentNode) { element.parentNode.removeChild(element); }
        if (done) { done(); }
        return;
      }
      element.inert = true;
      var card = element.querySelector('.modal__card');
      if (card) { card.animate([{ transform: 'none' }, { transform: 'scale(0.98)' }], { duration: 120, easing: 'ease-out', fill: 'forwards' }); }
      var animation = element.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 120, easing: 'ease-out', fill: 'forwards' });
      animation.onfinish = function () {
        if (element.parentNode) { element.parentNode.removeChild(element); }
        if (done) { done(); }
      };
    }

    // 預約下一次比對時長出來的條件；同一個事件處理完還沒用到就作廢。
    function expect(expectation) {
      expectations.push(expectation);
      global.setTimeout(function () {
        var index = expectations.indexOf(expectation);
        if (index >= 0) { expectations.splice(index, 1); }
      }, 0);
    }

    return {
      findLive: findLiveElement,
      begin: begin, play: play, morph: morph, still: still, enter: enter, fold: fold, setShown: setShown, swap: swap,
      dismiss: dismiss, expect: expect, removing: removing, shake: shakeNow, leave: leave, progress: progress, allowed: motionAllowed,
      // 加入的條件正由標籤帶過去時，中欄的捲動交給標籤（見下面的 bindArrival），移焦點的一方不要再瞬間捲過去。
      carries: function (element) { return !!carried && !!element && carried.has(element); }
    };
  })();
  global.JetMotion = Motion;

  /* ---- 加入的東西從哪裡來、到了哪裡（純呈現） ---------------------------------------------------
     使用者 2026-10-11 第三次調整時要求多參考 Apple 的設計思路，所以第五步按 KCT 卡片、欄位按鈕或「加入」時，
     看得到條件從按下的地方移進目前的條件組（Apple 的空間一致性）。使用者 2026-10-12 認為原本的做法像廉價的套版：
     一塊白板從卡片的大小一路長成整列的大小、字在半路淡掉、目的地先以霧狀出現，整個過程很突兀。所以改成拿起、帶過去、放下：
     - 按下時，一片小小的玻璃標籤在按下的名稱上浮起來，上面寫的就是新的一列的標題；
     - 標籤大小不變，沿直線移到新的一列的標題上，用上面的 SPRINGS.carry：玻璃的彈簧從靜止起步，起步俐落，約 0.24 秒到標題上
       （使用者 2026-10-13 認為原本先拱起再落下的弧線像在跳，所以不再走弧線）；
       目的地同時由 JetMotion 讓出空間，下面的內容跟著平順地讓開，不會一次跳開；
     - 到了標題上，標籤的字剛好疊在新的一列的標題上一起淡掉，玻璃也淡掉，像標籤被放進去。
     新的一列先向 JetMotion 預約：它照一般的方式長出來、內容淡入，只有標題空著，等標籤放下才出現。
     新的一列在中欄看得到的範圍外時（例如第二條以後的條件常在畫面下方），中欄跟著標籤用同一條曲線平順捲到新的一列，
     標籤飛向捲完之後的位置，兩者同時到；原本由移焦點造成的瞬間捲動因此不再發生（見 filter-step.js 的 revealAddedRule）。
     飛行途中使用者自己捲動時，標籤改跟著新的一列走，不再搶捲軸。減少動態或 Windows 高對比佈景時不飛，也不捲。
     這裡只畫一個不接收滑鼠的標籤與帶著中欄捲動，不改 state、不送 action，也不移動任何元件的位置。 */
  var ARRIVAL_SOURCES = [
    { trigger: '.filter-entry [data-kct-letter]', label: '.picker-card__label' },
    { trigger: '.filter-entry [data-add-field]' },
    { trigger: '.filter-entry [data-add-check]', within: '.filter-check', label: '.filter-check__label' },
    // 2026-10-16 加入的「預篩選」分頁沿用常用查核條件的列，按「加入」時同樣看得到標籤移進條件組。
    { trigger: '.filter-entry [data-add-prescreen]', within: '.filter-check', label: '.filter-check__label' }
  ];
  var ARRIVAL_ROWS = '.filter-workspace .scenario-flat__list > .rule-row';
  var ARRIVAL_TITLE = '.filter-rule-heading > strong';
  var ARRIVAL_LIFT = 70;      // 標籤在按下的地方浮起來
  var ARRIVAL_LEAVE = 30;     // 浮起一半時開始移動
  var ARRIVAL_TRAVEL = SPRINGS.carry.duration * 1000;   // 移到新的一列：彈簧看起來到了的時間
  var ARRIVAL_SETTLE = 110;   // 標籤淡掉、新的一列的標題淡入

  function bindArrival() {
    if (!canAnimate()) { return; }

    // 一段字第一行的位置。
    function textBox(element) {
      var range = document.createRange();
      range.selectNodeContents(element);
      var rects = range.getClientRects();
      var box = rects.length ? rects[0] : element.getBoundingClientRect();
      range.detach();
      return box;
    }

    function scroller() { return document.querySelector('.content'); }

    function chromeBottom() {
      var shell = document.querySelector('.app-shell');
      var value = shell ? parseFloat(global.getComputedStyle(shell).getPropertyValue('--chrome-h')) : NaN;
      return isFinite(value) ? value : 0;
    }

    // 落點寫成中欄內容裡的位置，所以標籤飛行途中中欄捲動時，仍然落在同一列上。
    function contentBox(element) {
      var area = scroller();
      var rect = textBox(element);
      if (!area) { return { left: rect.left, top: rect.top }; }
      var frame = area.getBoundingClientRect();
      return { left: rect.left - frame.left + area.scrollLeft, top: rect.top - frame.top + area.scrollTop };
    }

    function viewportPoint(box) {
      var area = scroller();
      if (!area) { return { x: box.left, y: box.top, frame: null }; }
      var frame = area.getBoundingClientRect();
      return { x: frame.left + box.left - area.scrollLeft, y: frame.top + box.top - area.scrollTop, frame: frame };
    }

    // 標籤移動的進度：SPRINGS.carry 是玻璃的彈簧（bounce 0.15），起步俐落，比不回彈的彈簧早一點到。
    // 標籤的字要剛好疊在標題上、中欄也不能捲過頭，所以走到 1 就停在那裡，不跟著彈簧超出去，捲動也不會多捲回來。
    function travelProgress(elapsed) {
      return Math.min(springProgress(SPRINGS.carry, elapsed), 1);
    }

    // 新的一列要整列看得到時，中欄該捲到哪裡：和 scrollIntoView({ block: 'nearest' }) 一樣只捲需要的距離；
    // 一列比看得到的範圍還高時對齊上緣，標題一定看得到。看得到的範圍不含上面的玻璃標題列。
    // row 是新的一列播完之後在中欄內容裡的上下緣，limit 是那時候最多能捲到哪裡。
    function scrollPlan(area, row, limit) {
      var frame = area.getBoundingClientRect();
      var top = Math.max(chromeBottom() - frame.top, 0) + 12;
      var bottom = 12;
      var start = area.scrollTop;
      var end = start;
      if (row.bottom - row.top > area.clientHeight - top - bottom || row.top < start + top) {
        end = row.top - top;
      } else if (row.bottom > start + area.clientHeight - bottom) {
        end = row.bottom - area.clientHeight + bottom;
      }
      end = Math.max(0, Math.min(end, limit));
      return Math.abs(end - start) < 1 ? null : { from: start, to: end, last: start, free: false };
    }

    // 標題先空著；放下時淡入。
    function hide(title) {
      return [title.animate([{ opacity: 0 }, { opacity: 0 }], { duration: 10000, fill: 'forwards' })];
    }

    function reveal(title) {
      title.animate([{ opacity: 0 }, { opacity: 1 }], { duration: ARRIVAL_SETTLE, easing: 'ease-out' });
    }

    function fly(source, row, target) {
      var title = row.querySelector(ARRIVAL_TITLE) || row;
      var titleStyle = global.getComputedStyle(title);
      // 新的一列照常長出來，只有標題空著，等標籤放下才淡入。
      var holds = hide(title);
      function release() { holds.forEach(function (hold) { hold.cancel(); }); holds = []; }
      if (carried) { carried.add(row); }
      var area = scroller();
      // 中欄要捲多少，等第一個畫面週期才算：那時重繪前的捲軸位置已經還原。
      var plan;
      var token = document.createElement('div');
      var label = document.createElement('span');
      token.className = 'arrival-token';
      token.setAttribute('aria-hidden', 'true');
      label.className = 'arrival-token__label';
      label.textContent = String(title.textContent || '').replace(/\s+/g, ' ').trim() || source.text;
      label.style.color = titleStyle.color;
      label.style.fontSize = titleStyle.fontSize;
      label.style.fontWeight = titleStyle.fontWeight;
      token.appendChild(label);
      document.body.appendChild(token);
      var outer = token.getBoundingClientRect();
      var inner = textBox(label);
      // 標籤的字從標籤左上角算起的位置：起點讓字對齊按下的名稱，終點讓字剛好疊在標題的字上。
      var offset = { x: inner.left - outer.left, y: inner.top - outer.top };
      var from = {
        x: source.box.left - offset.x,
        y: source.box.top + source.box.height / 2 - offset.y - inner.height / 2
      };
      var glass = token.animate([{ opacity: 0 }, { opacity: 1 }], {
        duration: ARRIVAL_LIFT, easing: 'ease-out', fill: 'forwards', pseudoElement: '::before'
      });
      var ink = label.animate([{ opacity: 0 }, { opacity: 1 }], { duration: ARRIVAL_LIFT, easing: 'ease-out', fill: 'forwards' });
      var started = motionNow();
      var lastFrame = 0;
      var landed = 0;
      var done = false;

      function remove() {
        glass.cancel();
        ink.cancel();
        if (token.parentNode) { token.parentNode.removeChild(token); }
      }

      function letGo() {
        if (carried) { carried.delete(row); }
      }

      // 很高的一列放下後，焦點所在的欄位若還在看得到的範圍外，再平順捲過去一點。
      function settleFocus() {
        var focused = document.activeElement;
        if (!area || !focused || !row.isConnected || !row.contains(focused)) { return; }
        var box = focused.getBoundingClientRect();
        var frame = area.getBoundingClientRect();
        if (box.top < Math.max(frame.top, chromeBottom()) || box.bottom > frame.bottom) {
          focused.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'smooth' });
        }
      }

      function finish() {
        if (done) { return; }
        done = true;
        release();
        remove();
        letGo();
        settleFocus();
      }

      // 新的一列在半路被重繪換掉時，標籤就地淡掉，不突然消失。
      function fadeAway() {
        if (done) { return; }
        done = true;
        release();
        letGo();
        token.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 120, easing: 'ease-out', fill: 'forwards' }).onfinish = remove;
      }

      // 快到時放下：標籤的字疊在同樣的標題上一起淡掉、標題淡入，玻璃先淡掉。
      function land() {
        landed = motionNow();
        release();
        reveal(title);
        glass.cancel();
        glass = token.animate([{ opacity: 1 }, { opacity: 0 }], {
          duration: ARRIVAL_SETTLE - 30, easing: 'ease-out', fill: 'forwards', pseudoElement: '::before'
        });
        ink.cancel();
        ink = label.animate([{ opacity: 1 }, { opacity: 0 }], { duration: ARRIVAL_SETTLE, easing: 'ease-in', fill: 'forwards' });
      }

      // 中欄跟著標籤捲，和標籤用同一條曲線、同時到；回傳還要捲的距離。使用者自己捲了就放手，不搶捲軸。
      function drive(eased) {
        if (!plan || plan.free) { return 0; }
        if (Math.abs(area.scrollTop - plan.last) > 2) { plan.free = true; return 0; }
        var wanted = plan.from + (plan.to - plan.from) * eased;
        area.scrollTop = wanted;
        plan.last = area.scrollTop;
        return plan.to - wanted;
      }

      function step() {
        if (done) { return; }
        if (!row.isConnected) { fadeAway(); return; }
        var now = motionNow();
        // 畫面卡住一下時（例如剛捲到的內容還在畫），標籤與捲動從卡住的地方接著走，不在下一格一下跳過一大段。
        if (lastFrame && now - lastFrame > 50) { started += now - lastFrame - 17; }
        lastFrame = now;
        var elapsed = now - started;
        if (plan === undefined) { plan = area ? scrollPlan(area, target.row, target.limit) : null; }
        var eased = travelProgress(elapsed - ARRIVAL_LEAVE);
        var remaining = drive(eased);
        var point = viewportPoint(target.title);
        if (!landed && !area && elapsed < 40 && (point.y < -40 || point.y > global.innerHeight + 40)) {
          // 沒有中欄可以捲、落點又看不到時不飛，新的一列照一般的方式出現。
          finish();
          reveal(title);
          return;
        }
        // 標籤飛向捲完之後新的一列標題的位置，所以一路平順，不會被捲動帶著上下晃。
        // 使用者 2026-10-13 指出標籤「以跳躍的形式」移到右側、看起來有點 goofy：原本的弧線先往上拱再落下，半路又放大一點。
        // 所以改成沿直線滑過去，大小不變，只靠彈簧的起步與放慢表現移動。
        var to = { x: point.x - offset.x, y: point.y - offset.y - remaining };
        var x = from.x + (to.x - from.x) * eased;
        var y = from.y + (to.y - from.y) * eased;
        // 浮起時從 0.98 長到原本大小，之後一路維持原本大小，字才會剛好疊在標題上。
        var rise = Math.min(elapsed / ARRIVAL_LIFT, 1);
        var scale = 0.98 + 0.02 * (1 - (1 - rise) * (1 - rise));
        token.style.transform = 'translate(' + x.toFixed(1) + 'px, ' + y.toFixed(1) + 'px) scale(' + scale.toFixed(4) + ')';
        // 標籤一到標題上就放下，不等彈簧的時間走完。
        if (!landed && (eased >= 1 || elapsed - ARRIVAL_LEAVE >= ARRIVAL_TRAVEL * 0.9)) { land(); }
        if (landed && now - landed >= ARRIVAL_SETTLE) { finish(); return; }
        global.requestAnimationFrame(step);
      }
      token.style.transform = 'translate(' + from.x.toFixed(1) + 'px, ' + from.y.toFixed(1) + 'px) scale(0.98)';
      global.requestAnimationFrame(step);
    }

    document.addEventListener('click', function (event) {
      var match = motionAllowed() ? matchTrigger(event.target, ARRIVAL_SOURCES) : null;
      if (!match || match.trigger.disabled) { return; }
      var holder = match.spec.within ? match.trigger.closest(match.spec.within) : match.trigger;
      var text = holder && match.spec.label ? holder.querySelector(match.spec.label) : match.trigger;
      if (!text) { return; }
      var source = { text: String(text.textContent || '').replace(/\s+/g, ' ').trim(), box: textBox(text) };
      if (!source.text || !source.box.width) { return; }
      Motion.expect({
        // 加入多條時落在「選取中」的那一列；沒有就用第一條新的。
        choose: function (elements) {
          var rows = elements.filter(function (element) { return element.matches(ARRIVAL_ROWS); });
          return rows.filter(function (element) { return element.classList.contains('rule-row--selected'); })[0] || rows[0] || null;
        },
        // 量的時候新的一列還是播完之後的大小：標題的位置、整列的上下緣與那時中欄最多能捲到哪裡，都寫成中欄內容裡的位置。
        measure: function (row) {
          var area = scroller();
          var box = row.getBoundingClientRect();
          var shift = area ? area.scrollTop - area.getBoundingClientRect().top : 0;
          return {
            title: contentBox(row.querySelector(ARRIVAL_TITLE) || row),
            row: { top: box.top + shift, bottom: box.bottom + shift },
            limit: area ? area.scrollHeight - area.clientHeight : 0
          };
        },
        start: function (row, target) { fly(source, row, target); }
      });
    }, true);
  }

  /* ---- 使用者要拿掉的東西（純呈現） ---------------------------------------------------
     使用者 2026-10-13 要求移除或清除東西時，要拿掉的東西先輕微快速地晃一下再收起來（見上面 JetMotion 的 removing）。
     移除鈕放在要拿掉的東西裡面時，在這裡記下：按下的那一刻找出按鈕外層的 scope，記下它的 data-motion-key；
     inside 為 true 表示拿掉的是 scope 裡面的東西（例如清除一組的條件），only 再限定是哪一種。
     按鈕不在要拿掉的東西裡面（例如再按一次 KCT 卡片），或要先確認、等 JetApi 回來才重繪（例如刪除案件）時，
     由畫面程式在確認的那一刻呼叫 JetMotion.removing。這裡只記下，不改 state、不送 action。 */
  var REMOVAL_SOURCES = [
    // 第五步：移除一條條件或子條件組、清除一組的條件、移除整組，以及確認移除已儲存的情境（等 JetApi 回來才重繪）。
    { trigger: '[data-action="remove-rule"], [data-compound-remove]', scope: '.rule-row' },
    { trigger: '[data-action="clear-set"]', scope: '.scenario-set', inside: true, only: '.rule-row' },
    { trigger: '[data-action="remove-set"]', scope: '.scenario-set' },
    { trigger: '[data-action="confirm-remove-scenario"]', scope: '.saved-scenario', wait: 15000 },
    // 第二步：移除待匯入的檔案。第三步：移除接受的過帳值。第四步：移除分類（有子分類時要先確認，確認由 validate-step.js 說明）。
    { trigger: '[data-action="pending-remove"]', scope: '.pending-row' },
    { trigger: '[data-remove-posting-value]', scope: '.value-chip' },
    { trigger: '[data-taxonomy-remove]', scope: '.taxonomy-row' },
    // 第三步：取消勾選一個攸關資料元素欄位、或「全部取消」時，收起的名稱與型別先晃一下（勾選時沒有東西消失，不會晃）。
    { trigger: '[data-rde-column]', scope: '.rde-field', inside: true, only: '.rde-field__controls' },
    { trigger: '[data-action="clear-all-rde"]', scope: '[data-motion-key="map-options-gl"]', inside: true, only: '.rde-field__controls' }
  ];

  function bindRemoval() {
    document.addEventListener('click', function (event) {
      var match = motionAllowed() ? matchTrigger(event.target, REMOVAL_SOURCES) : null;
      if (!match || match.trigger.disabled) { return; }
      var holder = match.trigger.closest(match.spec.scope || '[data-motion-key]');
      var key = holder ? holder.getAttribute('data-motion-key') : null;
      if (!key) { return; }
      Motion.removing(match.spec.inside
        ? { within: key, only: match.spec.only, wait: match.spec.wait }
        : { keys: key, wait: match.spec.wait });
    }, true);
  }

  /* ---- 視窗不在前景時（純呈現） -------------------------------------------------------------
     Apple 人機介面指引 Windows：不在作用中的視窗不使用 vibrancy，看起來比較收斂、離得比較遠。
     使用者切到 Excel 等其他程式時，標題列、表頭、選單與對話框的玻璃改成不透明、不模糊背景（app.css 的 html.window-inactive），
     回到 JE Tool 時恢復；切換本身不另加動態。只看視窗的 blur 與 focus 事件：頁面從沒取得焦點時維持平常的樣子。
     只改呈現，不改 state、不送 action。 */
  function bindWindowFocus() {
    var root = document.documentElement;
    if (!root || !root.classList || typeof global.addEventListener !== 'function') { return; }
    global.addEventListener('blur', function () { root.classList.add('window-inactive'); });
    global.addEventListener('focus', function () { root.classList.remove('window-inactive'); });
  }

  /* ---- 啟動 ---------------------------------------------------------------- */

  function init() {
    Store.subscribe(render);
    Store.subscribe(persistNewMessages);
    bindPressLight();
    bindChromeHeight();
    bindMessagesEdge();
    bindStatFit();
    bindGlide();
    bindArrival();
    bindRemoval();
    bindWindowFocus();

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
                Store.addMessage('目前的動作已完成，結果已保留；尚未開始的後續作業不會執行。', 'info');
              } else {
                // 進度接進訊息時拿掉「工作底稿：」的冒號（使用者 2026-10-14 第三輪勾選頁確認 R3-28）。
                var snapshot = latest.busyDetail
                  ? '，當時進度：' + Ui.progressInMessage(latest.busyDetail)
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
              Store.addMessage('取消作業失敗：' + error.message, 'error');
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
    // 不再記「畫面已載入」（使用者 2026-10-13 勾選頁確認刪除 7）。

    if (!ready) {
      bridgeProbeSettled = true;
      Store.addMessage('應用程式連線尚未就緒；目前只能顯示基本畫面。', 'error');
      render();
      return;
    }

    global.JetApi.systemPing({})
      .then(function (ping) {
        bridgeProbeSettled = true;
        Store.setBridgeReady(true);
        Store.setDevToolsEnabled(ping && ping.devToolsEnabled === true);
        // 背景查 SQL Server 後端身分，有設定時在訊息面板補一則（連到哪台、能否使用線上案件）。
        // 不阻塞專案載入——伺服器慢／不可達時 connect timeout 可能達數秒。
        global.JetApi.systemDatabaseInfo({}).then(function (info) {
          var s = info && info.sqlServer;
          // 建立案件的 SQL Server 選項只在確定沒有設定時停用；查詢失敗或還沒回來時維持可選。
          if (s && typeof s.configured === 'boolean') { Store.setSqlServerConfigured(s.configured); }
          // 沒有設定 SQL Server 時後端回空摘要，不加訊息（使用者 2026-10-13 勾選頁確認）。
          if (s && s.summary) {
            // 只有連不上 SQL Server 是需要注意：本機案件照常可用，不擋人。連上時不論版本都是一般訊息。
            Store.addMessage(s.summary, (s.configured && !s.reachable) ? 'warn' : 'info');
            // 容量提醒（純呈現）：整庫大小超過前端門檻時補一則一般訊息，審計員不需要採取行動。
            // schemaCount＝線上專案數（＝prj_ schema 數）。
            if (s.reachable && typeof s.databaseSizeMb === 'number' && s.databaseSizeMb > DB_CAPACITY_WARN_MB) {
              Store.addMessage(
                '資料庫容量偏大：單庫 ' + s.databaseSizeMb + ' MB（' + (s.schemaCount || 0) +
                ' 個線上案件），已超過 ' + DB_CAPACITY_WARN_MB + ' MB 預警門檻。',
                'info');
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
        Store.addMessage('應用程式連線失敗：' + error.message, 'error');
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
