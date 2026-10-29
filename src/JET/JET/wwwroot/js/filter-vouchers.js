/* 命中傳票清單：只呼叫後端分頁，不在前端算命中或排序；表頭排序與依傳票號碼查看都交給資料庫端。 */
(function (global) {
  'use strict';
  var Ui = global.JetUi, Api = global.JetApi;
  function count(value) { return Number(value || 0).toLocaleString(); }
  function positionsText(positions) {
    return positions.map(function (position) { return '第 ' + position.group + ' 組條件 ' + position.rule; }).join('、');
  }
  // 狀態欄只寫符合哪一組的哪一條，條件的完整讀回放在滑鼠停留的提示；長條件不再把每一列撐高，
  // 也不和上方的篩選條件重複（使用者 2026-10-29 指出常駐說明太多）。位置與說明都來自後端，畫面不自己判斷命中。
  function matchStatusHtml(row) {
    // Only mirror the backend's declared match scope; do not infer new per-row hit semantics.
    var voucherMatch = row.isHit && /傳票條件成立/.test(row.matchDescription || '') && !/^符合條件的分錄/.test(row.matchDescription || '');
    var label = row.isHit ? (voucherMatch ? '傳票條件成立' : '符合條件的分錄') : '參考分錄';
    var where = [['主要', row.primaryConditions], ['佐證', row.evidenceConditions], ['傳票條件', row.voucherConditions]]
      .filter(function (pair) { return pair[1] && pair[1].length; })
      .map(function (pair) { return pair[0] + '：' + positionsText(pair[1]); });
    var detail = where.length ? where.join('；') : (row.matchDescription || '');
    if (detail.indexOf(label) >= 0) { detail = ''; }
    if (!detail && !row.matchDescription) { return label; }
    return '<div class="filter-match-reason"' + (row.matchDescription ? ' title="' + Ui.esc(row.matchDescription) + '"' : '') + '><strong>' + label + '</strong>' +
      (detail ? '<p>' + Ui.esc(detail) + '</p>' : '') + '</div>';
  }
  // 使用者 2026-10-16 依過往底稿要求分開顯示「符合條件的傳票數量」與「符合條件的分錄筆數」。分錄筆數用後端的
  // summaryRowCount，口徑與條件篩選報告的摘要相同：只用「未預期借貸組合」一個條件時是整張傳票的分錄數，畫面不自己換算。
  // 統計列的數字從 28px 縮成 18px；情境之間由整列標題與下凹的底槽分開，數字比標題大也看得出屬於哪個情境。分錄筆數的口徑寫在那一格的提示裡；和一般情形不同時，
  // 才在統計列下方說一次（使用者 2026-10-29 指出常駐說明太多；定義放在用到的地方）。
  var WHOLE_VOUCHER_ROWS_NOTE = '只用「未預期借貸組合」一個條件時，分錄筆數包含整張傳票的分錄。';
  function summary(preview) {
    function stat(label, value, tip) {
      return '<div class="stat-card"' + (tip ? ' title="' + Ui.esc(tip) + '"' : '') + '><span class="stat-card__value">' + count(value) + '</span>' +
        '<span class="stat-card__label">' + label + '</span></div>';
    }
    return '<div class="stats-bar stats-bar--flush stats-bar--compact">' + stat('符合條件的傳票數量', preview.voucherCount) +
      stat('符合條件的分錄筆數', preview.summaryRowCount,
        preview.countsWholeVoucherRows ? WHOLE_VOUCHER_ROWS_NOTE : '標示為參考的分錄不計入分錄筆數。') + '</div>' +
      (preview.countsWholeVoucherRows && Number(preview.voucherCount) > 0
        ? '<p class="filter-count-note">' + Ui.esc(WHOLE_VOUCHER_ROWS_NOTE) + '</p>'
        : '') +
      '<div class="filter-voucher-host"></div>';
  }
  // app.js 的 JetMotion 比對用的識別：跟著同一份預覽結果，不用位置；草稿預覽與各個已存情境的清單可能同時在畫面上，各自一組。
  var motionIds = new WeakMap();
  var motionSerial = 0;
  function motionId(preview) {
    if (!motionIds.has(preview)) { motionSerial += 1; motionIds.set(preview, 'vouchers-' + motionSerial); }
    return motionIds.get(preview);
  }
  function mount(host, preview) {
    if (!host || !preview || !preview.voucherPage) { return; }
    var view = preview.__voucherView || { page: preview.voucherPage, cursor: null, history: [], open: null, detail: null, detailCursor: null, detailHistory: [], sort: null, search: '' };
    preview.__voucherView = view;
    if (!Object.prototype.hasOwnProperty.call(view, 'appliedSort')) {
      view.appliedSort = view.sort; view.appliedSearch = view.search;
    }
    view.generation = view.generation || 0;
    var id = motionId(preview);
    if (host.setAttribute && !(host.getAttribute && host.getAttribute('data-motion-key'))) { host.setAttribute('data-motion-key', id); }
    // 使用者 2026-10-13 要求展開與收合一致：換頁、展開或收合分錄只重畫這一塊，交給 JetMotion.morph，
    // 分錄明細從表格下方長出來、收合時收回，換頁時高度沿彈簧改變。第一次掛上時跟著整段重繪比對，不另外播。
    function redraw() {
      if (global.JetMotion && typeof global.JetMotion.morph === 'function') { global.JetMotion.morph(host, draw); } else { draw(); }
    }
    view.refresh = function () { if (host.isConnected) { redraw(); } };
    // 換開另一張傳票時，舊的分錄明細會留一份正在收回的副本（inert），排在新明細前面；
    // 焦點要跳過這份副本，否則落不到任何地方。只在這塊裡找，草稿預覽與儲存情境各有自己的一塊。
    function focus(selector) {
      if (global.JetFocus) {
        global.JetFocus.defer(function () {
          return Array.prototype.find.call(host.querySelectorAll(selector), function (el) {
            return !(el.closest && el.closest('[data-motion-leaving], [inert]'));
          }) || null;
        });
      }
    }
    function read(method, payload, accept) {
      if (view.loading) { return; }
      var token = ++view.generation;
      view.loading = true; view.error = ''; view.refresh();
      Ui.runBackground('讀取篩選傳票', function () {
        return method(payload).then(function (page) {
          if (token !== view.generation) { return; }
          view.loading = false;
          accept(page); view.appliedSort = view.sort; view.appliedSearch = view.search; view.retry = null; view.refresh();
        }).catch(function (error) {
          if (token !== view.generation) { return; }
          view.loading = false;
          view.sort = view.appliedSort; view.search = view.appliedSearch;
          view.error = Ui.errorText(error, '讀取失敗，請重試。');
          view.retry = function () {
            if (method === Api.queryFilterVoucherPage) { view.sort = payload.sort; view.search = payload.search || ''; }
            read(method, payload, accept);
          };
          view.refresh();
          throw error;
        });
      });
    }
    function voucherPage(cursor, backwards) {
      read(Api.queryFilterVoucherPage, Object.assign({}, preview.voucherRequest, { cursor: cursor, pageSize: Ui.TABLE_PAGE_SIZE, queryRevision: view.page.queryRevision,
        sort: view.sort, search: view.search || null }), function (page) {
        if (!backwards) { view.history.push(view.cursor); }
        else if (cursor === null) { view.history = []; }
        else { view.history.pop(); }
        // 後端只在第一頁計算總數；換頁沿用同一查詢第一頁的數字。
        if (page && page.totalCount == null && view.page) { page = Object.assign({}, page, { totalCount: view.page.totalCount }); }
        view.cursor = cursor; view.page = page; view.open = null; view.detail = null;
        focus('[data-voucher-open="0"]');
      });
    }
    // 換排序或搜尋：清掉上一頁紀錄，從第一頁重讀；游標綁定排序，不能沿用。
    function restart() { voucherPage(null, true); }
    function details(documentNumber, cursor, backwards) {
      read(Api.queryFilterVoucherRowsPage, Object.assign({}, preview.voucherRequest, { documentNumber: documentNumber,
        cursor: cursor, pageSize: Ui.TABLE_PAGE_SIZE, queryRevision: view.page.queryRevision }), function (page) {
        if (view.open !== documentNumber) { view.detailHistory = []; }
        else if (!backwards) { view.detailHistory.push(view.detailCursor); }
        else if (cursor === null) { view.detailHistory = []; }
        else { view.detailHistory.pop(); }
        if (page && page.totalCount == null && view.detail && view.open === documentNumber) {
          page = Object.assign({}, page, { totalCount: view.detail.totalCount });
        }
        view.open = documentNumber; view.detail = page; view.detailCursor = cursor;
        focus('[data-detail-close]');
      });
    }
    // 收起分錄：列上的「查看分錄」與明細標題都是同一個開關，收起後焦點回到那一列的按鈕。
    function closeDetails() {
      var index = view.page.rows.findIndex(function (row) { return row.documentNumber === view.open; });
      view.open = null; view.detail = null; redraw();
      if (index >= 0) { focus('[data-voucher-open="' + index + '"]'); }
    }
    function detailsPanelId() { return id + '-details'; }
    // 分錄明細的標題「傳票 X 的分錄」本身就是收合開關，箭頭在字前面，和其他整列標題的展開相同；
    // 原本收合是明細底部另一顆按鈕，使用者 2026-10-29 指出和展開的位置割裂後拿掉。
    function detailHtml() {
      if (!view.detail) { return ''; }
      var extra = (view.detail.columns || []).filter(function (column) { return column.isCustom; });
      // 金額與金額型額外欄位走共用的 Ui.money（兩位小數加千分位），與上方傳票總額同一格式。
      var extraCells = Ui.dynamicColumnCells(extra);
      function cellHtml(text, className) {
        return '<td' + (className ? ' class="' + className + '"' : '') + '>' + Ui.esc(text == null ? '' : text) + '</td>';
      }
      return '<div class="voucher-details" id="' + Ui.esc(detailsPanelId()) + '" data-motion-key="' + Ui.esc(id + '-details-' + view.open) + '">' +
        '<h4 class="voucher-details__title"><button type="button" class="voucher-details__toggle" data-detail-close' +
          Ui.disclosureAttributes(true, detailsPanelId()) + '>傳票 ' + Ui.esc(view.open) + ' 的分錄</button></h4>' +
        '<div class="preview-table__wrap"><table class="preview-table"><thead><tr>' +
        ['狀態', '傳票文件項次', '總帳日期', '傳票核准日', '科目編號', '科目名稱', '金額', '摘要'].concat(extra.map(function (column) { return column.label; })).map(function (label) { return '<th>' + Ui.esc(label) + '</th>'; }).join('') + '</tr></thead><tbody>' +
        view.detail.rows.map(function (row) { return '<tr class="' + (row.isHit ? 'filter-hit-row' : '') + '"><td>' + matchStatusHtml(row) + '</td>' +
          [row.lineItem, row.postDate, row.approvalDate, row.accountCode, row.accountName].map(function (value) { return cellHtml(value); }).join('') +
          cellHtml(Ui.money(row.amount), 'preview-table__amount') + cellHtml(row.description) +
          extraCells.map(function (column) { return cellHtml(column.cell(row), column.className); }).join('') + '</tr>'; }).join('') +
        '</tbody></table></div>' +
        Ui.pageControlsHtml(view.detailHistory.length + 1, view.detail.totalCount, {
          previousAttribute: 'data-detail-previous', nextAttribute: 'data-detail-next', canNext: !!view.detail.nextCursor, busy: view.loading
        }) + '</div>';
    }
    function headHtml() {
      var cells = Ui.sortableHeadCellsHtml('query.filterVoucherPage', [
        { key: 'documentNumber', label: '傳票號碼' }, { key: 'postDate', label: '最早總帳日期' },
        { key: 'hitRowCount', label: '符合條件的分錄', className: 'preview-table__amount' },
        { key: 'totalRowCount', label: '期間內全部分錄', className: 'preview-table__amount' },
        { key: 'voucherTotal', label: '傳票總額', className: 'preview-table__amount' }, { key: null, label: '查看內容' }]);
      return '<tr>' + cells + '</tr>';
    }
    function bindSortAndSearch() {
      host.querySelectorAll('th[data-sort-key]').forEach(function (th) {
        var key = th.getAttribute('data-sort-key');
        if (view.sort && view.sort.key === key) { th.setAttribute('aria-sort', view.sort.direction === 'asc' ? 'ascending' : 'descending'); }
        th.querySelector('.th-sort').onclick = function () {
          if (view.loading) { return; }
          var direction = view.sort && view.sort.key === key && view.sort.direction === 'asc' ? 'desc' : 'asc';
          view.sort = { key: key, direction: direction };
          restart();
        };
      });
      var form = host.querySelector('[data-page-search]');
      if (!form) { return; }
      var input = form.querySelector('[data-page-search-input]');
      var clear = form.querySelector('[data-page-search-clear]');
      input.value = view.search || '';
      clear.hidden = !view.search;
      form.onsubmit = function (event) { event.preventDefault(); if (view.loading) { return; } view.search = (input.value || '').trim(); restart(); };
      clear.onclick = function () {
        if (view.loading) { return; }
        // 使用者 2026-10-13 要求清除東西時先輕微快速地晃一下：輸入框不會拿掉，只是被清空，所以直接晃它。
        if (global.JetMotion && typeof global.JetMotion.shake === 'function') { global.JetMotion.shake(input); }
        view.search = ''; restart();
      };
    }
    function draw() {
      var page = view.page;
      var searchHtml = Ui.pageSearchHtml('依傳票號碼查看');
      var errorHtml = view.error ? '<p class="form-notice" role="alert">' + Ui.esc(view.error) +
        '</p><button type="button" class="btn btn--ghost btn--tiny" data-voucher-retry>重試</button>' : '';
      if (!page.rows.length) {
        host.innerHTML = (view.search ? searchHtml : '') + errorHtml + '<p class="empty-state" data-motion-key="' + Ui.esc(id + '-empty') + '">' + (view.search ? '沒有符合「' + Ui.esc(view.search) + '」的符合條件傳票。' : '沒有符合的傳票。調整條件後可再次查看。') + '</p>';
        bindSortAndSearch();
        var emptyRetry = host.querySelector('[data-voucher-retry]');
        if (emptyRetry) { emptyRetry.onclick = view.retry; }
        return;
      }
      host.innerHTML = searchHtml + errorHtml +
        '<div class="preview-table__wrap" data-motion-key="' + Ui.esc(id + '-page') + '"><table class="preview-table" aria-busy="' + !!view.loading + '"><thead>' + headHtml() + '</thead><tbody>' +
        // 筆數與金額靠右，表頭統一靠左。
        // 「查看分錄」是開關：展開中的那一列標示出來，箭頭朝下，再按一次收起。
        page.rows.map(function (row, index) {
          var open = !!view.detail && view.open === row.documentNumber;
          return '<tr' + (open ? ' class="voucher-row--open"' : '') + '><td>' + Ui.esc(row.documentNumber) + '</td><td>' + Ui.esc(row.postDate || '') + '</td><td class="preview-table__amount">' + count(row.hitRowCount) + '</td><td class="preview-table__amount">' + count(row.totalRowCount) +
            '</td><td class="preview-table__amount">' + Ui.money(row.voucherTotal) + '</td><td><button type="button" class="btn btn--ghost btn--tiny jet-disclosure" data-voucher-open="' + index + '"' +
            Ui.disclosureAttributes(open, open ? detailsPanelId() : null) + ' aria-label="查看傳票 ' + Ui.esc(row.documentNumber) + ' 的分錄">查看分錄</button></td></tr>';
        }).join('') +
        '</tbody></table></div>' + Ui.pageControlsHtml(view.history.length + 1, page.totalCount, {
          previousAttribute: 'data-voucher-previous', nextAttribute: 'data-voucher-next', canNext: !!page.nextCursor, busy: view.loading
        }) + detailHtml();
      bindSortAndSearch();
      var retry = host.querySelector('[data-voucher-retry]');
      if (retry) { retry.onclick = view.retry; }
      host.querySelectorAll('[data-voucher-open]').forEach(function (button) {
        button.onclick = function () {
          var documentNumber = page.rows[Number(button.dataset.voucherOpen)].documentNumber;
          if (view.detail && view.open === documentNumber) { closeDetails(); return; }
          details(documentNumber, null, true);
        };
      });
      host.querySelector('[data-voucher-next]').onclick = function () { voucherPage(page.nextCursor, false); };
      host.querySelector('[data-voucher-previous]').onclick = function () { voucherPage(view.history[view.history.length - 1], true); };
      if (view.detail) {
        var detailNext = host.querySelector('[data-detail-next]');
        var detailPrevious = host.querySelector('[data-detail-previous]');
        if (detailNext) { detailNext.onclick = function () { details(view.open, view.detail.nextCursor, false); }; }
        if (detailPrevious) { detailPrevious.onclick = function () { details(view.open, view.detailHistory[view.detailHistory.length - 1], true); }; }
        host.querySelector('[data-detail-close]').onclick = closeDetails;
      }
    }
    draw();
  }
  Ui.FilterVouchers = { summary: summary, mount: mount };
})(window);
