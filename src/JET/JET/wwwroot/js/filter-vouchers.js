/* 命中傳票清單：只呼叫後端分頁，不在前端算命中或排序；表頭排序與依傳票號碼查看都交給資料庫端。 */
(function (global) {
  'use strict';
  var Ui = global.JetUi, Api = global.JetApi;
  function positionsText(positions) {
    return positions.map(function (position) { return '第 ' + position.group + ' 組條件 ' + position.rule; }).join('、');
  }
  // 狀態欄只寫符合哪一組的哪一條，條件的完整讀回放在滑鼠停留的提示，也不和上方的篩選條件重複，
  // 常駐說明不再多寫。位置與說明都來自後端，畫面不自己判斷命中。
  // 名稱和位置排在同一行，太長時以省略號收尾，分錄列和傳票列一樣高。
  function matchStatusHtml(row) {
    // Only mirror the backend's declared match scope; do not infer new per-row hit semantics.
    var voucherMatch = row.isHit && /整張傳票條件成立/.test(row.matchDescription || '') && !/^符合條件的分錄/.test(row.matchDescription || '');
    var label = row.isHit ? (voucherMatch ? '整張傳票條件成立' : '符合條件的分錄') : '參考分錄';
    var where = [['主要', row.primaryConditions], ['傳票條件', row.voucherConditions]]
      .filter(function (pair) { return pair[1] && pair[1].length; })
      .map(function (pair) { return pair[0] + '：' + positionsText(pair[1]); });
    var detail = where.length ? where.join('；') : (row.matchDescription || '');
    if (detail.indexOf(label) >= 0) { detail = ''; }
    if (!detail && !row.matchDescription) { return label; }
    var tip = row.matchDescription || label + '，' + detail;
    return '<span class="filter-match-reason" title="' + Ui.esc(tip) + '"><strong>' + label + '</strong>' +
      (detail ? '<span class="filter-match-reason__where">' + Ui.esc(detail) + '</span>' : '') + '</span>';
  }
  // 依過往底稿分開顯示「符合條件的傳票數量」與「符合條件的分錄筆數」。分錄筆數用後端的
  // summaryRowCount，口徑與條件篩選報告的摘要相同：只用「未預期借貸組合」一個條件時是整張傳票的分錄數，畫面不自己換算。
  // 統計列的數字用 18px；情境之間由整列標題與下凹的底槽分開，數字比標題大也看得出屬於哪個情境。分錄筆數的口徑寫在那一格的提示裡；和一般情形不同時，
  // 才在統計列下方說一次；常駐說明不再多寫，定義放在用到的地方。
  var WHOLE_VOUCHER_ROWS_NOTE = '只用「未預期借貸組合」一個條件時，分錄筆數包含整張傳票的分錄。';
  function summary(preview) {
    function stat(label, value, tip) {
      return '<div class="stat-card"' + (tip ? ' title="' + Ui.esc(tip) + '"' : '') + '><span class="stat-card__value">' + Ui.countText(value || 0) + '</span>' +
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
    var view = preview.__voucherView || { page: preview.voucherPage, cursor: null, history: [], open: null, opening: null, detail: null, detailCursor: null, detailHistory: [], sort: null, search: '' };
    preview.__voucherView = view;
    if (!Object.prototype.hasOwnProperty.call(view, 'appliedSort')) {
      view.appliedSort = view.sort; view.appliedSearch = view.search;
    }
    view.generation = view.generation || 0;
    var id = motionId(preview);
    if (host.setAttribute && !(host.getAttribute && host.getAttribute('data-motion-key'))) { host.setAttribute('data-motion-key', id); }
    // 展開與收合要一致：換頁、展開或收合分錄只重畫這一塊，交給 JetMotion.morph，
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
          view.loading = false; view.opening = null;
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
        view.cursor = cursor; view.page = page; view.open = null; view.detail = null; view.opening = null;
        focus('[data-voucher-open="0"]');
      });
    }
    // 換排序或搜尋：清掉上一頁紀錄，從第一頁重讀；游標綁定排序，不能沿用。
    function restart() { voucherPage(null, true); }
    function rowIndexOf(voucherNumber) {
      return view.page.rows.findIndex(function (row) { return row.voucherNumber === voucherNumber; });
    }
    // 展開一張傳票：箭頭先轉過去並寫「正在讀取分錄…」，分錄讀回來後接在這張傳票下方。同時只開一張，焦點留在這一列的開關。
    function details(voucherNumber, cursor, backwards) {
      view.opening = voucherNumber;
      read(Api.queryFilterVoucherRowsPage, Object.assign({}, preview.voucherRequest, { voucherNumber: voucherNumber,
        cursor: cursor, pageSize: Ui.TABLE_PAGE_SIZE, queryRevision: view.page.queryRevision }), function (page) {
        if (view.open !== voucherNumber) { view.detailHistory = []; }
        else if (!backwards) { view.detailHistory.push(view.detailCursor); }
        else if (cursor === null) { view.detailHistory = []; }
        else { view.detailHistory.pop(); }
        if (page && page.totalCount == null && view.detail && view.open === voucherNumber) {
          page = Object.assign({}, page, { totalCount: view.detail.totalCount });
        }
        view.open = voucherNumber; view.detail = page; view.detailCursor = cursor; view.opening = null;
        focus('[data-voucher-open="' + rowIndexOf(voucherNumber) + '"]');
      });
    }
    function closeDetails() {
      var index = rowIndexOf(view.open);
      view.open = null; view.detail = null; view.opening = null; redraw();
      if (index >= 0) { focus('[data-voucher-open="' + index + '"]'); }
    }
    function entriesId(index) { return id + '-entries-' + index; }
    // 傳票表是同一張表：傳票是粗體的分組列，點整列就在下面展開這張傳票的分錄，分錄共用同一組欄位。
    // 表內不另放「查看分錄」按鈕，也不在表外另開一張分錄表。
    // 欄位：傳票號碼、項次、總帳日期、傳票核准日、會計科目、摘要、符合情形、金額，再接攸關資料元素的自訂欄。
    // 金額與金額型自訂欄走共用的 Ui.money（兩位小數加千分位），和傳票總額同一格式。
    // 每一列都只佔一行；只有分錄才有內容的欄由表頭先留好寬度，文字太長以省略號收尾、滑過看全文，
    // 展開或收合分錄時列高與欄寬都不變，其他傳票不會被擠開。
    function extraClass(column) { return column.valueType === 'amount' ? 'preview-table__amount voucher-table__amount' : 'voucher-table__extra'; }
    function headHtml(extra) {
      var cells = Ui.sortableHeadCellsHtml('query.filterVoucherPage', [
        { key: null, label: '', className: 'voucher-table__toggle' },
        { key: 'voucherNumber', label: '傳票號碼' }, { key: null, label: '項次', className: 'voucher-table__line' },
        { key: 'glDate', label: '總帳日期', className: 'voucher-table__date' },
        { key: null, label: '傳票核准日', className: 'voucher-table__date' }, { key: null, label: '會計科目', className: 'voucher-table__account' },
        { key: null, label: '摘要', className: 'voucher-table__description' },
        { key: 'hitRowCount', label: '符合情形', className: 'voucher-table__match' },
        { key: 'voucherTotal', label: '金額', className: 'preview-table__amount voucher-table__amount' }
      ].concat(extra.map(function (column) {
        return { key: null, label: column.label, className: extraClass(column) };
      })));
      return '<tr>' + cells + '</tr>';
    }
    function cellHtml(text, className) {
      return '<td' + (className ? ' class="' + className + '"' : '') + '>' + Ui.esc(text == null ? '' : text) + '</td>';
    }
    function clipCellHtml(text, className) {
      var value = text == null ? '' : String(text);
      return '<td class="' + className + '"><span class="voucher-table__clip"' + (value ? ' title="' + Ui.esc(value) + '"' : '') + '>' +
        Ui.esc(value) + '</span></td>';
    }
    function groupRowHtml(row, index, extra) {
      var open = view.open === row.voucherNumber && !!view.detail;
      var expanded = open || view.opening === row.voucherNumber;
      return '<tr class="voucher-table__group' + (expanded ? ' voucher-table__group--open' : '') + '" data-voucher-row="' + index + '" data-motion-key="' +
          Ui.esc(id + '-voucher-' + row.voucherNumber) + '">' +
        '<td class="voucher-table__toggle"><button type="button" class="voucher-table__open" data-voucher-open="' + index + '"' +
          Ui.disclosureAttributes(expanded, expanded ? entriesId(index) : null) + ' aria-label="傳票 ' + Ui.esc(row.voucherNumber) + ' 的分錄">' +
          '<span class="voucher-table__chevron" aria-hidden="true"></span></button></td>' +
        cellHtml(row.voucherNumber) + '<td></td>' + cellHtml(row.glDate || '') + '<td></td>' +
        '<td class="voucher-table__sub">共 ' + Ui.countText(row.totalRowCount || 0) + ' 筆分錄</td><td></td>' +
        '<td>符合 ' + Ui.countText(row.hitRowCount || 0) + ' 筆</td>' +
        cellHtml(Ui.money(row.voucherTotal), 'preview-table__amount') +
        extra.map(function () { return '<td></td>'; }).join('') +
      '</tr>';
    }
    // 展開中的傳票的分錄列；分錄超過一頁時，分頁放在這張傳票的最後一列。
    function entryRowsHtml(row, index, extra) {
      var columnCount = 9 + extra.length;
      if (view.opening === row.voucherNumber && !(view.open === row.voucherNumber && view.detail)) {
        return '<tr class="voucher-table__entry voucher-table__entry--last" id="' + Ui.esc(entriesId(index)) + '"><td></td>' +
          '<td class="voucher-table__sub" colspan="' + (columnCount - 1) + '">正在讀取分錄…</td></tr>';
      }
      if (view.open !== row.voucherNumber || !view.detail) { return ''; }
      var extraCells = Ui.dynamicColumnCells(extra);
      var entries = view.detail.rows;
      var pager = Ui.pageControlsHtml(view.detailHistory.length + 1, view.detail.totalCount, {
        previousAttribute: 'data-detail-previous', nextAttribute: 'data-detail-next', canNext: !!view.detail.nextCursor, busy: view.loading
      });
      var paged = view.detailHistory.length > 0 || !!view.detail.nextCursor;
      return entries.map(function (entry, entryIndex) {
        var last = !paged && entryIndex === entries.length - 1;
        return '<tr class="voucher-table__entry' + (entry.isHit ? ' voucher-table__entry--hit' : '') + (last ? ' voucher-table__entry--last' : '') + '"' +
            (entryIndex === 0 ? ' id="' + Ui.esc(entriesId(index)) + '"' : '') +
            ' data-motion-key="' + Ui.esc(id + '-entry-' + row.voucherNumber + '-' + (view.detailHistory.length + 1) + '-' + entryIndex) + '"><td></td><td></td>' +
          // 傳票文件項次自成一欄，表頭寫名詞表的短表頭「項次」，分錄列只寫數字，傳票號碼那一格留白。
          cellHtml(entry.lineItem, 'voucher-table__line') +
          cellHtml(entry.glDate) + cellHtml(entry.approvalDate) +
          clipCellHtml([entry.accountNumber, entry.accountName].filter(function (value) { return value; }).join(' '), 'voucher-table__account') +
          clipCellHtml(entry.description, 'voucher-table__description') +
          '<td class="voucher-table__match">' + matchStatusHtml(entry) + '</td>' +
          cellHtml(Ui.money(entry.amount), 'preview-table__amount') +
          extraCells.map(function (column, extraIndex) {
            return column.className ? cellHtml(column.cell(entry), column.className) : clipCellHtml(column.cell(entry), extraClass(extra[extraIndex]));
          }).join('') +
        '</tr>';
      }).join('') +
        (paged ? '<tr class="voucher-table__entry voucher-table__entry--last voucher-table__pager"><td></td>' +
          '<td colspan="' + (columnCount - 1) + '">' + pager + '</td></tr>' : '');
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
        // 清除東西時先輕微快速地晃一下：輸入框不會拿掉，只是被清空，所以直接晃它。
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
      var extra = (page.columns || []).filter(function (column) { return column.isCustom; });
      host.innerHTML = searchHtml + errorHtml +
        '<div class="preview-table__wrap" data-motion-key="' + Ui.esc(id + '-page') + '"><table class="preview-table voucher-table" aria-busy="' + !!view.loading + '"><thead>' +
          headHtml(extra) + '</thead><tbody>' +
        // 筆數與金額靠右，表頭統一靠左。
        page.rows.map(function (row, index) { return groupRowHtml(row, index, extra) + entryRowsHtml(row, index, extra); }).join('') +
        '</tbody></table></div>' + Ui.pageControlsHtml(view.history.length + 1, page.totalCount, {
          previousAttribute: 'data-voucher-previous', nextAttribute: 'data-voucher-next', canNext: !!page.nextCursor, busy: view.loading
        });
      bindSortAndSearch();
      var retry = host.querySelector('[data-voucher-retry]');
      if (retry) { retry.onclick = view.retry; }
      // 點傳票那一列的任何地方都是同一個開關；列首的按鈕給鍵盤與報讀軟體用，按下時同樣冒泡到這一列。
      host.querySelectorAll('[data-voucher-row]').forEach(function (tr) {
        tr.onclick = function () {
          if (view.loading) { return; }
          var voucherNumber = page.rows[Number(tr.getAttribute('data-voucher-row'))].voucherNumber;
          if (view.detail && view.open === voucherNumber) { closeDetails(); return; }
          details(voucherNumber, null, true);
        };
      });
      host.querySelector('[data-voucher-next]').onclick = function () { voucherPage(page.nextCursor, false); };
      host.querySelector('[data-voucher-previous]').onclick = function () { voucherPage(view.history[view.history.length - 1], true); };
      if (view.detail) {
        var detailNext = host.querySelector('[data-detail-next]');
        var detailPrevious = host.querySelector('[data-detail-previous]');
        if (detailNext) { detailNext.onclick = function () { details(view.open, view.detail.nextCursor, false); }; }
        if (detailPrevious) { detailPrevious.onclick = function () { details(view.open, view.detailHistory[view.detailHistory.length - 1], true); }; }
      }
    }
    draw();
  }
  Ui.FilterVouchers = { summary: summary, mount: mount };
})(window);
