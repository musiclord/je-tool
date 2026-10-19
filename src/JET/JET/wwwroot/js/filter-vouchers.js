/* 命中傳票清單：只呼叫後端分頁，不在前端算命中或排序；表頭排序與依傳票號碼查看都交給資料庫端。 */
(function (global) {
  'use strict';
  var Ui = global.JetUi, Api = global.JetApi;
  function count(value) { return Number(value || 0).toLocaleString(); }
  function matchStatusHtml(row) {
    // Only mirror the backend's declared match scope; do not infer new per-row hit semantics.
    var voucherMatch = row.isHit && /傳票條件成立/.test(row.matchDescription || '') && !/^符合條件的分錄/.test(row.matchDescription || '');
    var label = row.isHit ? (voucherMatch ? '傳票條件成立' : '符合條件的分錄') : '參考分錄';
    return row.matchDescription
      ? '<div class="filter-match-reason"><strong>' + label + '</strong><p>' + Ui.esc(row.matchDescription) + '</p></div>'
      : label;
  }
  // 使用者 2026-10-16 依過往底稿要求分開顯示「符合條件的傳票數量」與「符合條件的分錄筆數」。分錄筆數用後端的
  // summaryRowCount，口徑與條件篩選報告的摘要相同：只用「未預期借貸組合」一個條件時是整張傳票的分錄數，畫面不自己換算。
  function summary(preview) {
    function stat(label, value) {
      return '<div class="stat-card"><span class="stat-card__value">' + count(value) + '</span>' +
        '<span class="stat-card__label">' + label + '</span></div>';
    }
    var note = preview.countsWholeVoucherRows
      ? '只用「未預期借貸組合」一個條件時，整張傳票的分錄都計入分錄筆數，與條件篩選報告及工作底稿相同。'
      : '標示為參考的分錄不計入分錄筆數。';
    return '<div class="stats-bar stats-bar--flush">' + stat('符合條件的傳票數量', preview.voucherCount) +
      stat('符合條件的分錄筆數', preview.summaryRowCount) + '</div>' +
      (Number(preview.voucherCount) > 0 ? '<p class="rule-card__sub">展開傳票可查看查核期間內的全部分錄。' + note + '</p>' : '') +
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
        focus('.voucher-details h4');
      });
    }
    function detailHtml() {
      if (!view.detail) { return ''; }
      var extra = (view.detail.columns || []).filter(function (column) { return column.isCustom; });
      // 金額與金額型額外欄位走共用的 Ui.money（兩位小數加千分位），與上方傳票總額同一格式。
      var extraCells = Ui.dynamicColumnCells(extra);
      function cellHtml(text, className) {
        return '<td' + (className ? ' class="' + className + '"' : '') + '>' + Ui.esc(text == null ? '' : text) + '</td>';
      }
      return '<div class="voucher-details" data-motion-key="' + Ui.esc(id + '-details-' + view.open) + '"><h4 tabindex="-1">傳票 ' + Ui.esc(view.open) + ' 的分錄</h4><div class="preview-table__wrap"><table class="preview-table"><thead><tr>' +
        ['狀態', '傳票文件項次', '總帳日期', '傳票核准日', '科目編號', '科目名稱', '金額', '摘要'].concat(extra.map(function (column) { return column.label; })).map(function (label) { return '<th>' + Ui.esc(label) + '</th>'; }).join('') + '</tr></thead><tbody>' +
        view.detail.rows.map(function (row) { return '<tr class="' + (row.isHit ? 'filter-hit-row' : '') + '"><td>' + matchStatusHtml(row) + '</td>' +
          [row.lineItem, row.postDate, row.approvalDate, row.accountCode, row.accountName].map(function (value) { return cellHtml(value); }).join('') +
          cellHtml(Ui.money(row.amount), 'preview-table__amount') + cellHtml(row.description) +
          extraCells.map(function (column) { return cellHtml(column.cell(row), column.className); }).join('') + '</tr>'; }).join('') +
        '</tbody></table></div><div class="panel__actions panel__actions--start">' +
        Ui.pageControlsHtml(view.detailHistory.length + 1, view.detail.totalCount, {
          previousAttribute: 'data-detail-previous', nextAttribute: 'data-detail-next', canNext: !!view.detail.nextCursor, busy: view.loading
        }) +
        '<button type="button" class="btn btn--ghost" data-detail-close>收合分錄</button></div></div>';
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
        page.rows.map(function (row, index) { return '<tr><td>' + Ui.esc(row.documentNumber) + '</td><td>' + Ui.esc(row.postDate || '') + '</td><td class="preview-table__amount">' + count(row.hitRowCount) + '</td><td class="preview-table__amount">' + count(row.totalRowCount) +
          '</td><td class="preview-table__amount">' + Ui.money(row.voucherTotal) + '</td><td><button type="button" class="btn btn--ghost btn--tiny" data-voucher-open="' + index + '">展開分錄</button></td></tr>'; }).join('') +
        '</tbody></table></div>' + Ui.pageControlsHtml(view.history.length + 1, page.totalCount, {
          previousAttribute: 'data-voucher-previous', nextAttribute: 'data-voucher-next', canNext: !!page.nextCursor, busy: view.loading
        }) + detailHtml();
      bindSortAndSearch();
      var retry = host.querySelector('[data-voucher-retry]');
      if (retry) { retry.onclick = view.retry; }
      host.querySelectorAll('[data-voucher-open]').forEach(function (button) { button.onclick = function () { details(page.rows[Number(button.dataset.voucherOpen)].documentNumber, null, true); }; });
      host.querySelector('[data-voucher-next]').onclick = function () { voucherPage(page.nextCursor, false); };
      host.querySelector('[data-voucher-previous]').onclick = function () { voucherPage(view.history[view.history.length - 1], true); };
      if (view.detail) {
        host.querySelector('[data-detail-next]').onclick = function () { details(view.open, view.detail.nextCursor, false); };
        host.querySelector('[data-detail-previous]').onclick = function () { details(view.open, view.detailHistory[view.detailHistory.length - 1], true); };
        host.querySelector('[data-detail-close]').onclick = function () {
          var index = page.rows.findIndex(function (row) { return row.documentNumber === view.open; });
          view.open = null; view.detail = null; redraw(); focus('[data-voucher-open="' + index + '"]');
        };
      }
    }
    draw();
  }
  Ui.FilterVouchers = { summary: summary, mount: mount };
})(window);
