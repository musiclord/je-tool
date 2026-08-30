/*
  開發者面板：診斷日誌匯出 NDJSON（dev-only，非審計 workflow）。
  後端 dev.log.export 回診斷日誌 ring buffer 的完整 NDJSON（action/SQL/transaction/exception/milestone），
  以唯讀 textarea 呈現可複製字串，供開發測試把完整系統真相交給 AI 驗證。
*/
(function (global) {
  'use strict';

  var Ui = global.JetUi;

  function initDevLogPanel() {
    var body = Ui.$('dev-log-panel-body');
    if (!body) { return; }

    body.innerHTML =
      '<div class="dev-panel__controls">' +
        '<button type="button" class="btn btn--ghost" data-action="dev-log-refresh">重新整理</button>' +
        '<button type="button" class="btn btn--ghost" data-action="dev-log-copy">複製</button>' +
        '<span class="dev-panel__info" data-bind="dev-log-info">尚未載入。</span>' +
      '</div>' +
      '<textarea class="dev-panel__log" data-bind="dev-log-text" readonly rows="16" spellcheck="false" ' +
        'placeholder="按「重新整理」載入完整診斷日誌（NDJSON）…"></textarea>';

    body.querySelector('[data-action="dev-log-refresh"]').addEventListener('click', devLogRefresh);
    body.querySelector('[data-action="dev-log-copy"]').addEventListener('click', devLogCopy);
  }

  function devLogRefresh() {
    Ui.run('匯出診斷日誌', function () {
      return global.JetApi.devLogExport({}).then(function (data) {
        var ndjson = (data && data.ndjson) || '';
        var textarea = Ui.$('dev-log-text');
        if (textarea) { textarea.value = ndjson; }
        var count = ndjson ? ndjson.split('\n').length : 0;
        Ui.setText('dev-log-info', '共 ' + count + ' 筆（NDJSON）');
      });
    });
  }

  function devLogCopy() {
    var textarea = Ui.$('dev-log-text');
    if (!textarea || !textarea.value) {
      Ui.setText('dev-log-info', '尚無內容可複製（請先「重新整理」）。');
      return;
    }

    function done() {
      Ui.setText('dev-log-info', '已複製（' + textarea.value.split('\n').length + ' 列）。');
    }

    function fallback() {
      textarea.focus();
      textarea.select();
      try {
        global.document.execCommand('copy');
        done();
      } catch (e) {
        Ui.setText('dev-log-info', '請手動全選並複製（Ctrl+A、Ctrl+C）。');
      }
    }

    if (global.navigator && global.navigator.clipboard && global.navigator.clipboard.writeText) {
      global.navigator.clipboard.writeText(textarea.value).then(done, fallback);
    } else {
      fallback();
    }
  }

  Ui.initDevLogPanel = initDevLogPanel;
})(window);
