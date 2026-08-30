/*
  開發者面板：資料庫唯讀檢視（dev-only，非審計 workflow；provider 標籤依後端動態顯示）。
*/
(function (global) {
  'use strict';

  var Ui = global.JetUi;

  // 區域狀態（dev-only，不進 Store）
  var dev = { tables: [], selected: null, offset: 0, limit: 50, totalCount: 0 };

  function initDevPanel() {
    var body = Ui.$('dev-panel-body');
    if (!body) { return; }

    body.innerHTML =
      '<div class="dev-panel__controls">' +
        '<button type="button" class="btn btn--ghost" data-action="dev-refresh">重新整理</button>' +
        '<select class="dev-panel__select" data-bind="dev-table-select"></select>' +
        '<button type="button" class="btn btn--ghost" data-action="dev-query">查詢</button>' +
        '<button type="button" class="btn btn--ghost" data-action="dev-prev" disabled>上一頁</button>' +
        '<button type="button" class="btn btn--ghost" data-action="dev-next" disabled>下一頁</button>' +
      '</div>' +
      '<p class="dev-panel__info" data-bind="dev-info">尚未載入。</p>' +
      '<div class="dev-panel__grid" data-bind="dev-grid"></div>';

    body.querySelector('[data-action="dev-refresh"]').addEventListener('click', devRefresh);
    body.querySelector('[data-action="dev-query"]').addEventListener('click', function () {
      dev.offset = 0;
      devQuery();
    });
    body.querySelector('[data-action="dev-prev"]').addEventListener('click', function () {
      dev.offset = Math.max(dev.offset - dev.limit, 0);
      devQuery();
    });
    body.querySelector('[data-action="dev-next"]').addEventListener('click', function () {
      dev.offset += dev.limit;
      devQuery();
    });
  }

  function devRefresh() {
    Ui.run('讀取資料庫總覽', function () {
      return global.JetApi.devDbOverview({}).then(function (data) {
        dev.tables = data.tables || [];

        var select = Ui.$('dev-table-select');
        select.innerHTML = dev.tables.map(function (t) {
          return '<option value="' + Ui.esc(t.name) + '">' + Ui.esc(t.name) + '（' + t.rowCount + '）</option>';
        }).join('');

        // provider 標籤只用於 dev-info 行(面板標題維持 provider 中立,見 index.html)。
        var providerLabels = { sqlServer: 'SQL Server ', duckdb: 'DuckDB ', sqlite: 'SQLite ' };
        var providerLabel = providerLabels[data.databaseProvider] || (data.databaseProvider + ' ');
        var sizeKb = Math.round((data.fileSizeBytes || 0) / 102.4) / 10;
        Ui.setText('dev-info',
          data.databasePath + ' ｜ ' + providerLabel + data.engineVersion + ' ｜ ' + sizeKb + ' KB');
      });
    });
  }

  function devQuery() {
    var select = Ui.$('dev-table-select');
    if (!select || !select.value) { return; }

    dev.selected = select.value;

    Ui.run('查詢資料表', function () {
      return global.JetApi.devDbTableData({
        tableName: dev.selected,
        limit: dev.limit,
        offset: dev.offset
      }).then(function (data) {
        dev.totalCount = data.totalCount;
        renderDevGrid(data);

        var prev = document.querySelector('[data-action="dev-prev"]');
        var next = document.querySelector('[data-action="dev-next"]');
        prev.disabled = dev.offset <= 0;
        next.disabled = dev.offset + dev.limit >= data.totalCount;

        Ui.setText('dev-info',
          data.tableName + ' ｜ 共 ' + data.totalCount + ' 列 ｜ 顯示 ' +
          (data.totalCount === 0 ? 0 : data.offset + 1) + ' – ' +
          Math.min(data.offset + data.limit, data.totalCount));
      });
    });
  }

  function renderDevGrid(data) {
    var grid = Ui.$('dev-grid');
    if (!grid) { return; }

    var head = '<tr>' + data.columns.map(function (c) {
      return '<th>' + Ui.esc(c) + '</th>';
    }).join('') + '</tr>';

    var body = data.rows.map(function (row) {
      return '<tr>' + row.map(function (cell) {
        return cell === null
          ? '<td class="is-null">NULL</td>'
          : '<td>' + Ui.esc(cell) + '</td>';
      }).join('') + '</tr>';
    }).join('');

    grid.innerHTML =
      '<table class="dev-table"><thead>' + head + '</thead><tbody>' +
      (body || '<tr><td colspan="' + data.columns.length + '">（無資料）</td></tr>') +
      '</tbody></table>';
  }

  Ui.initDevPanel = initDevPanel;
})(window);
