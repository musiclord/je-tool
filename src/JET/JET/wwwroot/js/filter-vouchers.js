/* 命中傳票清單：只呼叫後端分頁，不在前端算命中或排序；表頭排序與依傳票號碼查看都交給資料庫端。 */
(function (global) {
  'use strict';
  var Ui = global.JetUi, Api = global.JetApi;
  function count(value) { return Number(value || 0).toLocaleString(); }
  function matchStatusHtml(row) {
    var label = row.isHit ? '命中分錄' : '參考分錄';
    return row.matchDescription
      ? '<details class="filter-match-reason"><summary title="展開查看原因">' + label + '</summary><p>' + Ui.esc(row.matchDescription) + '</p></details>'
      : label;
  }
  function summary(preview) {
    return '<p class="rule-card__sub">符合條件：' + count(preview.count) + ' 筆分錄（' + count(preview.voucherCount) +
      ' 張傳票）</p>' +
      (Number(preview.voucherCount) > 0 ? '<details class="rule-card__sub"><summary>結果說明</summary>' +
      '<p>展開傳票可查看查核期間內的全部分錄。標示為參考的分錄不計入上方筆數。</p></details>' : '') +
      '<div class="filter-voucher-host"></div>';
  }
  function mount(host, preview) {
    if (!host || !preview || !preview.voucherPage) { return; }
    var view = preview.__voucherView || { page: preview.voucherPage, cursor: null, history: [], open: null, detail: null, detailCursor: null, detailHistory: [], sort: null, search: '' };
    preview.__voucherView = view;
    var generation = 0;
    function focus(selector) {
      if (global.JetFocus) { global.JetFocus.defer(function () { return host.querySelector(selector); }); }
    }
    function read(method, payload, accept) {
      var token = ++generation;
      Ui.runBackground('讀取篩選傳票', function () {
        return method(payload).then(function (page) {
          if (host.isConnected && token === generation) { accept(page); draw(); }
        }).catch(function (error) {
          if (!host.isConnected || token !== generation) { return; }
          host.innerHTML = '<p class="form-notice" role="alert">' + Ui.esc(error.message || '讀取失敗，請重新預覽。') + '</p>';
          throw error;
        });
      });
    }
    function voucherPage(cursor, backwards) {
      read(Api.queryFilterVoucherPage, Object.assign({}, preview.voucherRequest, { cursor: cursor, pageSize: 50, queryRevision: view.page.queryRevision,
        sort: view.sort, search: view.search || null }), function (page) {
        if (!backwards) { view.history.push(view.cursor); if (view.history.length > 100) { view.history.shift(); } }
        view.cursor = cursor; view.page = page; view.open = null; view.detail = null;
        focus('[data-voucher-open="0"]');
      });
    }
    // 換排序或搜尋：清掉上一頁紀錄，從第一頁重讀；游標綁定排序，不能沿用。
    function restart() { view.history = []; voucherPage(null, true); }
    function details(documentNumber, cursor, backwards) {
      read(Api.queryFilterVoucherRowsPage, Object.assign({}, preview.voucherRequest, { documentNumber: documentNumber,
        cursor: cursor, pageSize: 50, queryRevision: view.page.queryRevision }), function (page) {
        if (view.open !== documentNumber) { view.detailHistory = []; }
        else if (!backwards) { view.detailHistory.push(view.detailCursor); if (view.detailHistory.length > 100) { view.detailHistory.shift(); } }
        view.open = documentNumber; view.detail = page; view.detailCursor = cursor;
        focus('.voucher-details h4');
      });
    }
    function detailHtml() {
      if (!view.detail) { return ''; }
      var extra = (view.detail.columns || []).filter(function (column) { return column.isCustom; });
      return '<div class="voucher-details"><h4 tabindex="-1">傳票 ' + Ui.esc(view.open) + ' 的分錄</h4><div class="preview-table__wrap"><table class="preview-table"><thead><tr>' +
        ['狀態', '列號', '總帳日期', '核准日期', '科目代號', '科目名稱', '金額', '摘要'].concat(extra.map(function (column) { return column.label; })).map(function (label) { return '<th>' + Ui.esc(label) + '</th>'; }).join('') + '</tr></thead><tbody>' +
        view.detail.rows.map(function (row) { return '<tr class="' + (row.isHit ? 'filter-hit-row' : '') + '"><td>' + matchStatusHtml(row) + '</td>' +
          [row.lineItem, row.postDate, row.approvalDate, row.accountCode, row.accountName, String(row.amount), row.description]
            .concat(extra.map(function (column) { return (row.customValues || {})[column.key]; }))
            .map(function (value) { return '<td>' + Ui.esc(value == null ? '' : value) + '</td>'; }).join('') + '</tr>'; }).join('') +
        '</tbody></table></div><div class="panel__actions"><button type="button" class="btn btn--ghost" data-detail-previous' + (!view.detailHistory.length ? ' disabled' : '') + '>上一頁分錄</button>' +
        '<button type="button" class="btn btn--ghost" data-detail-next' + (!view.detail.nextCursor ? ' disabled' : '') + '>下一頁分錄</button>' +
        '<button type="button" class="btn btn--ghost" data-detail-close>收合分錄</button></div></div>';
    }
    function headHtml() {
      var cells = Ui.sortableHeadCellsHtml('query.filterVoucherPage', [
        { key: 'documentNumber', label: '傳票號碼' }, { key: 'postDate', label: '最早總帳日期' },
        { key: 'hitRowCount', label: '命中分錄' }, { key: 'totalRowCount', label: '期間內全部分錄' },
        { key: 'voucherTotal', label: '傳票總額', className: 'preview-table__amount' }, { key: null, label: '查看內容' }]);
      return '<tr>' + cells + '</tr>';
    }
    function bindSortAndSearch() {
      host.querySelectorAll('th[data-sort-key]').forEach(function (th) {
        var key = th.getAttribute('data-sort-key');
        if (view.sort && view.sort.key === key) { th.setAttribute('aria-sort', view.sort.direction === 'asc' ? 'ascending' : 'descending'); }
        th.querySelector('.th-sort').onclick = function () {
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
      form.onsubmit = function (event) { event.preventDefault(); view.search = (input.value || '').trim(); restart(); };
      clear.onclick = function () { view.search = ''; restart(); };
    }
    function draw() {
      var page = view.page;
      var searchHtml = Ui.pageSearchHtml('依傳票號碼查看');
      if (!page.rows.length) {
        host.innerHTML = (view.search ? searchHtml : '') + '<p class="empty-state">' + (view.search ? '沒有符合「' + Ui.esc(view.search) + '」的命中傳票。' : '沒有符合的傳票。調整條件後可再次查看。') + '</p>';
        bindSortAndSearch();
        return;
      }
      host.innerHTML = searchHtml + '<div class="preview-table__wrap"><table class="preview-table"><thead>' + headHtml() + '</thead><tbody>' +
        page.rows.map(function (row, index) { return '<tr><td>' + Ui.esc(row.documentNumber) + '</td><td>' + Ui.esc(row.postDate || '') + '</td><td>' + count(row.hitRowCount) + '</td><td>' + count(row.totalRowCount) +
          '</td><td class="preview-table__amount">' + Ui.money(row.voucherTotal) + '</td><td><button type="button" class="btn btn--ghost btn--tiny" data-voucher-open="' + index + '">展開分錄</button></td></tr>'; }).join('') +
        '</tbody></table></div><div class="panel__actions"><button type="button" class="btn btn--ghost" data-voucher-first' + (view.cursor === null ? ' disabled' : '') + '>第一頁</button>' +
        '<button type="button" class="btn btn--ghost" data-voucher-previous' + (!view.history.length ? ' disabled' : '') + '>上一頁傳票</button>' +
        '<button type="button" class="btn btn--ghost" data-voucher-next' + (!page.nextCursor ? ' disabled' : '') + '>下一頁傳票</button></div>' + detailHtml();
      bindSortAndSearch();
      host.querySelectorAll('[data-voucher-open]').forEach(function (button) { button.onclick = function () { details(page.rows[Number(button.dataset.voucherOpen)].documentNumber, null, true); }; });
      host.querySelector('[data-voucher-next]').onclick = function () { voucherPage(page.nextCursor, false); };
      host.querySelector('[data-voucher-previous]').onclick = function () { voucherPage(view.history.pop(), true); };
      host.querySelector('[data-voucher-first]').onclick = function () { view.history = []; voucherPage(null, true); };
      if (view.detail) {
        host.querySelector('[data-detail-next]').onclick = function () { details(view.open, view.detail.nextCursor, false); };
        host.querySelector('[data-detail-previous]').onclick = function () { details(view.open, view.detailHistory.pop(), true); };
        host.querySelector('[data-detail-close]').onclick = function () {
          var index = page.rows.findIndex(function (row) { return row.documentNumber === view.open; });
          view.open = null; view.detail = null; draw(); focus('[data-voucher-open="' + index + '"]');
        };
      }
    }
    draw();
  }
  Ui.FilterVouchers = { summary: summary, mount: mount };
})(window);
