/*
  資料預覽（正式版功能；query.dataPreview）。
  讓使用者隨時看到目前操作的資料長什麼樣子：
  - 欄位配對時對照「欄名 ↔ 實際內容」（來源原貌資料集）
  - 進階篩選前掌握數值/日期/摘要的大概樣貌（標準化後資料集 + 概況統計）
  有界預覽（≤50 列 + 總列數），絕不載入完整母體；權威計算一律在後端。

  第二波呈現層：常駐右欄面板（頁籤切換資料表＋橫向捲動表格）。
  - 資料集選擇由下拉改為頁籤：PBC／標準化的 GL、TB 與科目配對直接可見，其餘收進「其他資料」選單；
    現行支援的全部資料集在新 UI 都可達（openDataPreview 對任一 key 都能定位並載入）。
  - 載入節制：切頁籤／步驟按鈕（openDataPreview）才呼叫 query.dataPreview；
    另在首次進入 workflow 且有專案時自動載入一次（沿用舊 <details> 首開自動載入的節制模式）。
  - 底層資料世代刷新：後端寫入成功且改變可預覽資料集後（匯入落地、配對提交、科目配對／授權
    清單／行事曆匯入），state.dataGeneration 遞增；本面板據此作廢快取——可見即自動重抓當前
    作用資料集，收合則待展開再抓。世代訊號只在真實資料落地時跳（非每個 action），故不會對大母體
    高頻打擊。詳見 initDataPreview 的訂閱者。
  - query.dataPreview 的 payload／回應欄位一個都不改；不新增後端 action。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  // wire key → 顯示標籤。直接頁籤逐字鏡射 Domain DataPreviewDatasetLabels；
  // DATASETS 的選單與載入狀態使用相同的業務名稱。
  var DATASETS = [
    { value: 'glStaging', label: 'GL 原始資料' },
    { value: 'tbStaging', label: 'TB 原始資料' },
    { value: 'glEntries', label: '納入測試的分錄' },
    { value: 'glExcludedEntries', label: '未納入測試的分錄' },
    { value: 'tbBalances', label: '已確認配對的試算表' },
    { value: 'accountMappings', label: '科目配對' },
    { value: 'authorizedPreparers', label: '授權編製人員清單' },
    { value: 'dateDimension', label: '行事曆（假日與補班日）' },
    { value: 'schemaOverview', label: '資料表摘要' }
  ];

  // 受查者提供資料（PBC）與標準化資料成對直接呈現；科目配對是驗證步驟的主要工作資料，
  // 同樣保持直接可見，避免步驟按鈕換位後只亮「其他資料」而看不出目前資料集。
  var MAIN_TABS = [
    { value: 'glStaging', label: 'GL 原始資料' },
    { value: 'glEntries', label: '納入測試的分錄' },
    { value: 'tbStaging', label: 'TB 原始資料' },
    { value: 'tbBalances', label: '已確認配對的試算表' },
    { value: 'accountMappings', label: '科目配對' }
  ];

  // 主頁籤以外的既有資料集（一個都不少）：收進「其他資料」選單，使用相同的業務名稱。
  var MORE = DATASETS.filter(function (d) {
    return !MAIN_TABS.some(function (t) { return t.value === d.value; });
  });

  // 固定欄位 id → 顯示標籤（query.dataPreview）
  var COLUMN_LABELS = {
    documentNumber: '傳票號碼',
    lineItem: '項次',
    postDate: '總帳入帳日',
    accountCode: '科目編號',
    accountName: '科目名稱',
    documentDescription: '摘要',
    amount: '金額',
    drCr: '借貸',
    manualAuto: '人工/自動',
    postingStatus: '過帳狀態',
    exclusionReason: '未納入原因',
    changeAmount: '變動金額',
    standardizedCategory: '科目分類',
    preparerName: '人員代號或姓名',
    // dateDimension
    date: '日期',
    dayType: '類別',
    dayName: '說明',
    // schemaOverview（結構總覽，rows 來自 catalog metadata）
    canonicalName: '正規名',
    physicalName: '實體名',
    layer: '層',
    audience: '用途',
    browsable: '可瀏覽'
  };

  // dateDimension 的 dayType 原值（holiday/makeup）→ 中文顯示
  var DAY_TYPE_LABELS = { holiday: '假日', makeup: '補班' };

  // 未納入分錄測試範圍的原因（後端 closed wire value）→ 中文顯示；未登錄值原樣呈現。
  var EXCLUSION_REASON_LABELS = { period: '日期不符', postingStatus: '過帳狀態不符' };

  // 純顯示層的 cell 改寫（不改後端語意）：dayType 原值轉中文；會計金額欄一律走 Ui.money（兩位小數加千分位）。
  function displayCell(dataset, column, cell) {
    if (cell === null) { return null; }
    if (dataset === 'glEntries' && column === 'manualAuto') { return { manual: '人工', automatic: '自動' }[cell] || cell; }
    if (dataset === 'dateDimension' && column === 'dayType') {
      return DAY_TYPE_LABELS[cell] || cell;
    }
    // 標準化資料集的會計金額欄（GL amount、TB changeAmount）一律兩位小數加千分位，與左側篩選預覽同一 Ui.money 呈現。
    // 明確綁定資料集＋欄 id：原貌（glStaging）用來源欄名保真呈現、其數值欄可能是項次等非金額，故不套此格式，
    // 也不會因某來源欄剛好叫 amount 而誤中。
    if (dataset === 'glExcludedEntries' && column === 'exclusionReason') {
      return EXCLUSION_REASON_LABELS[cell] || cell;
    }
    if ((dataset === 'glEntries' && column === 'amount') ||
        (dataset === 'glExcludedEntries' && column === 'amount') ||
        (dataset === 'tbBalances' && column === 'changeAmount')) {
      return Ui.money(cell);
    }
    return cell;
  }

  // 面板節點（init 時快取；皆為 index.html 靜態子節點）。
  var elAside = null;
  var elTabs = null;
  var elBody = null;   // == Ui.$('data-preview-body')
  var elNote = null;
  var elResize = null; // 左邊界拖曳把手

  var RESIZE_MIN = 300; // 最窄可拖到的寬度（再窄請用收合鈕）；預設 436、上限依視窗推算。

  var activeDataset = MAIN_TABS[0].value; // 預設受查者提供的 GL 原始資料
  var menuOpen = false;                   // 「其他資料」選單是否展開
  var compactMediaQuery = null;           // 半屏模式的唯一 viewport 判定
  var autoCollapsed = false;              // 只有系統收合者，回到寬螢幕才自動恢復
  var loadedOnce = false;                 // 首次自動載入的節制旗標
  var lastDataGeneration = 0;             // 上次已反映的 state.dataGeneration（底層資料世代基準）
  var lastCollapsed = false;              // 上次的收合狀態（用於偵測「收合→展開」的邊緣）
  var dirty = false;                      // 收合期間發生資料世代變動，展開時補抓一次
  var refreshQueued = false;              // microtask 合併旗標：同一同步輪的多次觸發只抓一次
  var previewResponseGuard = Ui.createLatestResponseGuard();

  function resetDataPreviewState() {
    previewResponseGuard.invalidate();
    activeDataset = MAIN_TABS[0].value;
    loadedOnce = false;
    lastDataGeneration = 0;
    lastCollapsed = false;
    dirty = false;
    refreshQueued = false;
    menuOpen = false;
    if (elBody) {
      elBody.removeAttribute('aria-busy');
      elBody.removeAttribute('data-preview-state');
      elBody.innerHTML = '<p class="data-preview__hint">正在切換專案；新案件資料載入後會自動更新預覽。</p>';
    }
    if (elNote) { elNote.innerHTML = ''; }
    syncTabs();
  }

  Ui.registerWorkflowReset(resetDataPreviewState);

  // 訂閱者不直接 refresh，改排進 microtask 並以 refreshQueued 去重。理由：applyLoadedProject
  // 是同步函式，會連呼 setProject＋6 個 bumpData setter；從 create-step 進案（建立案件）時
  // view 已是 'workflow'，每次 notify 都會同步進訂閱者——直呼 refresh 會單次進案
  // 連發約 7 次 query.dataPreview（互相 supersede、末次才算數，違反「不盲目重抓」原則）。
  // 合併後同一同步輪只 flush 一次；flush 時重讀「當下」state 決定抓不抓：
  //   已離開 workflow／專案已清 → 放棄（picker 期間不噴後端）；
  //   收合 → 只標 dirty 待展開再抓；可見 → refresh()（內部清 dirty）。
  // 「同世代不雙抓」由訂閱者的 lastDataGeneration 把關不變，flush 本身不動世代基準。
  function scheduleRefresh() {
    if (refreshQueued) { return; }
    refreshQueued = true;
    Promise.resolve().then(function () {
      refreshQueued = false;
      var state = Store.getState();
      if (state.view !== 'workflow' || !state.project) { return; }
      if (state.dataPreviewCollapsed) { dirty = true; return; }
      refresh();
    });
  }

  function initDataPreview() {
    elAside = Ui.$('data-preview');
    if (!elAside) { return; }
    elTabs = elAside.querySelector('.data-preview__tabs');
    elBody = Ui.$('data-preview-body');
    elNote = elAside.querySelector('.data-preview__note');

    renderTabs();
    if (elBody) {
      elBody.innerHTML = '<p class="data-preview__hint">切換上方資料表以載入預覽。</p>';
    }

    initPreviewResize();
    initCompactPreviewMode();

    // 收合鈕 → 收合；窄豎條 → 展開（純 UI，經 Store 讓 app.js 切 class）。
    var collapseBtn = elAside.querySelector('.data-preview__collapse');
    if (collapseBtn) {
      collapseBtn.addEventListener('click', function () { setPreviewCollapsedByUser(true); });
    }
    var rail = elAside.querySelector('.data-preview__rail');
    if (rail) {
      rail.addEventListener('click', function () { setPreviewCollapsedByUser(false); });
      rail.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setPreviewCollapsedByUser(false); }
      });
    }

    // 頁籤／其他資料選單以事件委派處理；DOM 只建立一次，開關時不替換目前焦點節點。
    if (elTabs) {
      elTabs.addEventListener('click', onTabsClick);
      elTabs.addEventListener('keydown', onTabsKeydown);
    }
    if (elBody) {
      elBody.addEventListener('click', function (event) {
        var retry = event.target.closest('[data-action="retry-data-preview"]');
        if (!retry || retry.getAttribute('data-dataset') !== activeDataset) { return; }
        refresh();
      });
    }

    // 點面板外關閉「其他資料」選單。
    document.addEventListener('click', function (e) {
      if (menuOpen && (!elTabs || !elTabs.contains(e.target))) { closeMenu(); }
    });

    // 資料預覽的刷新時機（節制與世代雙軌）：
    //  1) 首次進入 workflow 且已有專案：自動載入一次（沿用舊 <details> 首開節制），並同步世代基準。
    //  2) 底層資料世代（state.dataGeneration）變動——匯入落地／配對提交等後端寫入成功後：
    //     作廢資料快取；面板可見即自動重抓當前作用資料集，收合則標記待展開再抓
    //     （不對大母體高頻打擊；世代訊號只在真實資料落地時跳，非每個 action）。
    //  3) 由收合切換為展開，且期間曾發生世代變動：補抓一次，確保展開看到的是新資料。
    // 三路觸發一律經 scheduleRefresh（microtask 合併）：同一同步輪不論來了幾次 notify／世代跳
    // （picker→openProject 的 applyLoadedProject、或 create-step 在 view 已是 workflow 時進案皆然），
    // 都只對後端抓一次。
    Store.subscribe(function (state) {
      // 不在 workflow 或無專案（picker／載入中）：重置節制旗標，下次進 workflow 由首載分支重抓一次。
      // picker 路徑的專案載入（applyLoadedProject）期間 dataGeneration 連跳但 view 仍是 picker，
      // 於此略過；create-step 路徑（view 已是 workflow）的連跳則靠 scheduleRefresh 合併。
      if (state.view !== 'workflow' || !state.project) {
        loadedOnce = false;
        dirty = false;
        lastCollapsed = state.dataPreviewCollapsed;
        return;
      }

      // 首次（或切換專案後重新）進入 workflow：同步世代基準並排一次載入。
      // loadedOnce 就地設立（不等 flush）：同一同步輪的後續 notify 直接走世代分支，只更新基準、不重複排程。
      if (!loadedOnce) {
        loadedOnce = true;
        lastDataGeneration = state.dataGeneration;
        lastCollapsed = state.dataPreviewCollapsed;
        scheduleRefresh();
        return;
      }

      // 底層資料世代變動：作廢資料快取，排程重抓（可見與否由 flush 時的當下狀態決定）。
      if (state.dataGeneration !== lastDataGeneration) {
        lastDataGeneration = state.dataGeneration;
        scheduleRefresh();
      }

      // 由收合切換為展開，且期間曾發生世代變動：補抓一次，確保展開看到的是新資料。
      if (lastCollapsed && !state.dataPreviewCollapsed && dirty) {
        scheduleRefresh();
      }
      lastCollapsed = state.dataPreviewCollapsed;
    });
  }

  function initCompactPreviewMode() {
    if (typeof global.matchMedia !== 'function') { return; }
    compactMediaQuery = global.matchMedia('(max-width: 1360px)');

    function applyCompactState(isCompact) {
      if (isCompact) {
        // 已由使用者收合者不改寫來源；否則記為系統收合，寬螢幕時才可自動恢復。
        if (!Store.getState().dataPreviewCollapsed) {
          autoCollapsed = true;
          Store.setDataPreviewCollapsed(true);
        }
        return;
      }
      if (autoCollapsed) {
        autoCollapsed = false;
        Store.setDataPreviewCollapsed(false);
      }
    }

    applyCompactState(compactMediaQuery.matches);
    compactMediaQuery.addEventListener('change', function (event) {
      applyCompactState(event.matches);
    });
  }

  function setPreviewCollapsedByUser(collapsed) {
    autoCollapsed = false;
    Store.setDataPreviewCollapsed(collapsed);
  }

  // 上限依視窗寬度推算：留給左欄目錄與中欄文件流足夠空間（約 420px），並封在 300～絕對上限之間。
  function clampPreviewWidth(px) {
    var max = Math.max(RESIZE_MIN, window.innerWidth - 420);
    return Math.min(max, Math.max(RESIZE_MIN, Math.round(px)));
  }

  // 左邊界拖曳把手：拖動改 --data-preview-width（inline，純 UI，不進 state／後端）。
  // 面板在右側，把手向左拖＝加寬。用 pointer capture，指標拖出把手仍持續追蹤。
  // 雙擊還原預設寬度（移除 inline 變數，回退 CSS 的 436px）。
  function initPreviewResize() {
    if (!elAside) { return; }
    elResize = elAside.querySelector('.data-preview__resize');
    if (!elResize) { return; }

    var dragging = false;
    var startX = 0;
    var startWidth = 0;

    elResize.addEventListener('pointerdown', function (e) {
      if (Store.getState().dataPreviewCollapsed) { return; } // 收合態把手已隱藏，保險再擋
      dragging = true;
      startX = e.clientX;
      startWidth = elAside.getBoundingClientRect().width;
      try { elResize.setPointerCapture(e.pointerId); } catch (_) { /* 少數環境無 capture，退化為一般拖曳 */ }
      document.body.classList.add('is-resizing-preview');
      e.preventDefault();
    });

    elResize.addEventListener('pointermove', function (e) {
      if (!dragging) { return; }
      var next = clampPreviewWidth(startWidth + (startX - e.clientX));
      elAside.style.setProperty('--data-preview-width', next + 'px');
    });

    function endDrag(e) {
      if (!dragging) { return; }
      dragging = false;
      try { elResize.releasePointerCapture(e.pointerId); } catch (_) { /* 同上，忽略 */ }
      document.body.classList.remove('is-resizing-preview');
    }
    elResize.addEventListener('pointerup', endDrag);
    elResize.addEventListener('pointercancel', endDrag);

    elResize.addEventListener('dblclick', function () {
      elAside.style.removeProperty('--data-preview-width');
    });
  }

  function onTabsClick(e) {
    var toggle = e.target.closest('[data-more-toggle]');
    if (toggle) {
      e.stopPropagation();
      if (menuOpen) { closeMenu(); } else { openMenu(); }
      return;
    }
    var item = e.target.closest('[data-ds]');
    if (item) {
      var fromMenu = !!item.closest('[role="menu"]');
      closeMenu({ restoreFocus: fromMenu });
      selectDataset(item.getAttribute('data-ds'));
    }
  }

  function otherMenuItems() {
    return elTabs ? Array.prototype.slice.call(elTabs.querySelectorAll('[role="menuitem"]')) : [];
  }

  function focusOtherMenuItem(index) {
    var items = otherMenuItems();
    if (items.length === 0) { return; }
    var bounded = (index + items.length) % items.length;
    items[bounded].focus();
  }

  function onTabsKeydown(e) {
    var toggle = e.target.closest('[data-more-toggle]');
    var menuItem = e.target.closest('[role="menuitem"]');

    if (toggle && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
      e.preventDefault();
      openMenu();
      focusOtherMenuItem(e.key === 'ArrowDown' ? 0 : -1);
      return;
    }
    if (e.key === 'Escape' && (toggle || menuItem || menuOpen)) {
      e.preventDefault();
      closeMenu({ restoreFocus: true });
      return;
    }
    if (!menuItem) { return; }

    var items = otherMenuItems();
    var index = items.indexOf(menuItem);
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      focusOtherMenuItem(index + (e.key === 'ArrowDown' ? 1 : -1));
    } else if (e.key === 'Home' || e.key === 'End') {
      e.preventDefault();
      focusOtherMenuItem(e.key === 'Home' ? 0 : -1);
    } else if (e.key === 'Tab') {
      closeMenu();
    }
  }

  function openMenu() {
    if (menuOpen) { return; }
    menuOpen = true;
    syncTabs();
  }

  function closeMenu(options) {
    if (!menuOpen) { return; }
    menuOpen = false;
    syncTabs();
    if (options && options.restoreFocus && elTabs) {
      var toggle = elTabs.querySelector('[data-more-toggle]');
      if (toggle) { toggle.focus(); }
    }
  }

  // 切換作用中資料集：更新頁籤態並載入該資料集（使用者動作才呼叫後端）。
  function selectDataset(dataset) {
    if (!dataset) { return; }
    activeDataset = dataset;
    syncTabs();
    refresh();
  }

  function renderTabs() {
    if (!elTabs) { return; }

    var mainHtml = MAIN_TABS.map(function (t) {
      var active = t.value === activeDataset ? ' is-active' : '';
      return '<button type="button" class="data-preview__tab' + active + '" data-ds="' + t.value +
        '" role="tab" aria-selected="' + (active ? 'true' : 'false') + '">' + Ui.esc(t.label) + '</button>';
    }).join('');

    var moreActive = MORE.some(function (d) { return d.value === activeDataset; });
    var menuHtml = MORE.map(function (d) {
      var active = d.value === activeDataset ? ' is-active' : '';
      return '<button type="button" class="data-preview__menu-item' + active +
        '" data-ds="' + d.value + '" role="menuitem">' + Ui.esc(d.label) + '</button>';
    }).join('');

    var moreHtml =
      '<span class="data-preview__more">' +
        '<button type="button" class="data-preview__tab data-preview__tab--more' + (moreActive ? ' is-active' : '') +
          '" data-more-toggle aria-haspopup="menu" aria-controls="data-preview-other-menu" aria-expanded="' +
          (menuOpen ? 'true' : 'false') + '">其他資料 ▾</button>' +
        '<div class="data-preview__menu" id="data-preview-other-menu" role="menu"' +
          (menuOpen ? '' : ' hidden') + '>' + menuHtml + '</div>' +
      '</span>';

    elTabs.innerHTML = mainHtml + moreHtml;
  }

  function syncTabs() {
    if (!elTabs) { return; }
    elTabs.querySelectorAll('[data-ds]').forEach(function (item) {
      var active = item.getAttribute('data-ds') === activeDataset;
      item.classList.toggle('is-active', active);
      if (item.getAttribute('role') === 'tab') {
        item.setAttribute('aria-selected', active ? 'true' : 'false');
      }
    });
    var moreActive = MORE.some(function (dataset) { return dataset.value === activeDataset; });
    var toggle = elTabs.querySelector('[data-more-toggle]');
    var menu = elTabs.querySelector('[role="menu"]');
    if (toggle) {
      toggle.classList.toggle('is-active', moreActive);
      toggle.setAttribute('aria-expanded', menuOpen ? 'true' : 'false');
    }
    if (menu) { menu.hidden = !menuOpen; }
  }

  // 步驟內的「預覽資料」按鈕入口：展開右欄、切到對應資料集並載入。
  // 簽名不變（既有 steps/*.js 以 dataset key 呼叫；四個主頁籤與「其他資料」皆覆蓋全部 key）。
  function openDataPreview(dataset) {
    if (!elAside) { return; }
    setPreviewCollapsedByUser(false);
    selectDataset(dataset);
  }

  function previewDatasetLabel(dataset) {
    var direct = MAIN_TABS.find(function (item) { return item.value === dataset; });
    if (direct) { return direct.label; }
    var known = DATASETS.find(function (item) { return item.value === dataset; });
    return known ? known.label : dataset;
  }

  function clearPreviewRequestState() {
    if (!elBody) { return; }
    elBody.removeAttribute('aria-busy');
    elBody.removeAttribute('data-preview-state');
  }

  function renderPreviewLoading(dataset) {
    if (!elBody) { return; }
    elBody.setAttribute('aria-busy', 'true');
    elBody.setAttribute('data-preview-state', 'loading');
    elBody.innerHTML =
      '<div class="data-preview__status" role="status">' +
        '<strong>正在載入「' + Ui.esc(previewDatasetLabel(dataset)) + '」</strong>' +
        '<span>預覽完成前不顯示上一份資料。</span>' +
      '</div>';
    if (elNote) { elNote.innerHTML = ''; }
  }

  function renderPreviewError(dataset, error) {
    if (!elBody) { return; }
    clearPreviewRequestState();
    elBody.setAttribute('data-preview-state', 'error');
    var message = error && error.message ? error.message : '無法讀取這份資料預覽。';
    elBody.innerHTML =
      '<div class="data-preview__status data-preview__status--error" role="alert">' +
        '<strong>載入失敗</strong>' +
        '<span>' + Ui.esc(message) + '</span>' +
        '<button type="button" class="btn btn--ghost btn--tiny" data-action="retry-data-preview" ' +
          'data-dataset="' + Ui.esc(dataset) + '">重試</button>' +
      '</div>';
    if (elNote) { elNote.innerHTML = ''; }
  }

  function refresh() {
    if (!Store.getState().project) {
      loadedOnce = false; // 尚無專案，等有專案再由訂閱者自動載入一次
      if (elBody) {
        clearPreviewRequestState();
        elBody.innerHTML = '<p class="data-preview__hint">尚未建立或載入專案；建立或載入專案後即可預覽資料。</p>';
      }
      if (elNote) { elNote.innerHTML = ''; }
      return;
    }

    loadedOnce = true; // 同步設立，避免訂閱者重入
    dirty = false;     // 本次已抓最新，清掉待補抓標記
    var dataset = activeDataset;
    var requestState = Store.getState();
    var projectId = requestState.project.projectId;
    var dataGeneration = requestState.dataGeneration;
    var acceptResponse = previewResponseGuard.issue(function () {
      var latest = Store.getState();
      return latest.view === 'workflow' && !!latest.project &&
        latest.project.projectId === projectId &&
        latest.dataGeneration === dataGeneration &&
        activeDataset === dataset;
    });
    renderPreviewLoading(dataset);

    // 唯讀、可併行的背景讀取：走 runBackground 而非 run——資料預覽的世代自動刷新會在某個變更型
    // 作業的 run 仍持有 busy 期間觸發（匯入／配對提交／載入專案完成後的 bumpData），若走 run 會被
    // single-flight 靜默吞掉而看到陳舊預覽；後端 query.dataPreview 屬 concurrent、可安全併行。
    // 使用者切頁籤發生在非 busy 時（作業進行中右欄已被 inert 擋住），此路徑一致不設整介面遮罩。
    Ui.runBackground('載入資料預覽', function () {
      return global.JetApi.queryDataPreview({ dataset: dataset }).then(function (data) {
        if (acceptResponse()) { renderPreview(dataset, data); }
      }).catch(function (error) {
        // 跨案／跨世代的舊請求連錯誤也不污染新案件訊息；當前請求才交回 runBackground 呈現。
        if (acceptResponse()) {
          renderPreviewError(dataset, error);
          throw error;
        }
      });
    });
  }

  // 判斷各欄是否為數字欄（純顯示層：預覽列全非空值皆為數字才右對齊）。
  function isNumericValue(v) {
    if (typeof v === 'number') { return true; }
    if (typeof v === 'string' && v !== '') { return /^-?[\d,]+(\.\d+)?$/.test(v); }
    return false;
  }

  function detectNumericColumns(columns, rows) {
    var flags = [];
    for (var c = 0; c < columns.length; c++) {
      var hasValue = false;
      var allNumeric = true;
      for (var r = 0; r < rows.length; r++) {
        var cell = rows[r][c];
        if (cell === null || cell === undefined || cell === '') { continue; }
        hasValue = true;
        if (!isNumericValue(cell)) { allNumeric = false; break; }
      }
      flags.push(hasValue && allNumeric);
    }
    return flags;
  }

  function renderPreview(dataset, data) {
    if (!elBody) { return; }
    clearPreviewRequestState();

    var columns = (data && data.columns) || [];
    var rows = (data && data.rows) || [];

    if (!data || data.totalCount === 0) {
      elBody.innerHTML = '<p class="data-preview__hint">' + Ui.esc(emptyHint(dataset)) + '</p>';
      renderNote(data);
      return;
    }

    var numeric = detectNumericColumns(columns, rows);

    var head = '<tr>' + columns.map(function (c, i) {
      return '<th' + (numeric[i] ? ' class="num"' : '') + '>' + Ui.esc(COLUMN_LABELS[c] || c) + '</th>';
    }).join('') + '</tr>';

    var body = rows.map(function (row) {
      return '<tr>' + row.map(function (cell, i) {
        var shown = displayCell(dataset, columns[i], cell);
        if (shown === null || shown === undefined) {
          return '<td class="is-null' + (numeric[i] ? ' num' : '') + '">—</td>';
        }
        return '<td' + (numeric[i] ? ' class="num"' : '') + '>' + Ui.esc(shown) + '</td>';
      }).join('') + '</tr>';
    }).join('');

    elBody.innerHTML =
      '<table class="data-preview__table"><thead>' + head + '</thead><tbody>' + body + '</tbody></table>';

    renderNote(data);
  }

  // 底部註記：左＝真實列數／欄數（不寫死示意數字），右＝橫向捲動提示；
  // glEntries 有概況統計時另補一列（供進階篩選前把關數值／日期區間）。
  function renderNote(data) {
    if (!elNote) { return; }
    if (!data) { elNote.innerHTML = ''; return; }

    var shown = (data.rows && data.rows.length) || 0;
    var total = Number(data.totalCount || 0).toLocaleString();
    var cols = (data.columns && data.columns.length) || 0;
    var count = '前 ' + shown + ' 列，共 ' + total + ' 列，全 ' + cols + ' 欄';

    // stats 的形狀依資料集而異，由後端決定：標準化分錄回金額／日期／傳票概況，
    // 未納入本次測試的分錄回兩類排除計數。畫面只複述既有欄位，不跨資料集猜欄位。
    var statsHtml = '';
    if (data.stats && data.stats.excludedByPeriodCount != null) {
      statsHtml = '<div class="data-preview__stats">' + Ui.esc(
        '日期不符 ' + Number(data.stats.excludedByPeriodCount).toLocaleString() +
        ' 筆 ｜ 過帳狀態不符 ' + (data.stats.excludedByPostingStatusCount == null ? '—' : Number(data.stats.excludedByPostingStatusCount).toLocaleString()) + ' 筆'
      ) + '</div>';
    } else if (data.stats) {
      var parts = ['金額（絕對值）' + Ui.money(data.stats.amountAbsMin) +
        ' ～ ' + Ui.money(data.stats.amountAbsMax)];
      if (data.stats.postDateMin) {
        parts.push('總帳入帳日 ' + data.stats.postDateMin + ' ～ ' + data.stats.postDateMax);
      }
      parts.push('傳票 ' + Number(data.stats.voucherCount).toLocaleString() + ' 張');
      statsHtml = '<div class="data-preview__stats">' + Ui.esc(parts.join(' ｜ ')) + '</div>';
    }

    elNote.innerHTML =
      statsHtml +
      '<div class="data-preview__note-row">' +
        '<span class="data-preview__count">' + Ui.esc(count) + '</span>' +
        '<span class="data-preview__scroll-hint">← 橫向捲動看全部欄位 →</span>' +
      '</div>';
  }

  function emptyHint(dataset) {
    switch (dataset) {
      case 'glExcludedEntries':
        return '目前沒有未納入測試的分錄：已確認配對的分錄均已納入本次測試。';
      case 'glStaging': return '尚未匯入 GL 資料；完成「匯入資料」後即可預覽來源原貌。';
      case 'tbStaging': return '尚未匯入 TB 資料；完成「匯入資料」後即可預覽來源原貌。';
      case 'glEntries': return '總帳明細尚未確認配對；確認配對後即可預覽納入測試的分錄。';
      case 'tbBalances': return '試算表尚未確認配對；確認配對後即可預覽。';
      case 'accountMappings': return '尚未儲存科目配對；請在「資料驗證與測試」的科目清單選擇分類並儲存。';
      case 'authorizedPreparers': return '尚未匯入授權編製人員清單；在「匯入資料」步驟匯入後即可預覽。';
      case 'dateDimension': return '尚未匯入事務所假日／補班日；在「匯入資料」步驟匯入行事曆後即可預覽。';
      default: return '目前沒有資料。';
    }
  }

  Ui.initDataPreview = initDataPreview;
  Ui.openDataPreview = openDataPreview;
})(window);
