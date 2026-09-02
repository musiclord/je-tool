/*
  開發者面板：診斷日誌單鍵輸出（dev-only，非審計 workflow）。
  按「輸出日誌」發 dev.log.exportFile：後端把完整診斷日誌（檔案 sink 全量；sink 不可讀時退
  ring buffer 並標記 source）寫成 .txt 到目前案件資料夾，回傳實際路徑與筆數。完整 DEV 日誌可能
  含 SQL 與參數值，只供本機開發診斷；可分享給支援人員／agent 的版本請用「輸出支援日誌」。
*/
(function (global) {
  'use strict';

  var Ui = global.JetUi;

  function initDevLogPanel() {
    var body = Ui.$('dev-log-panel-body');
    if (!body) { return; }

    body.innerHTML =
      '<div class="dev-panel__controls">' +
        '<button type="button" class="btn btn--ghost" data-action="dev-log-export-file">輸出日誌</button>' +
        '<span class="dev-panel__info" data-bind="dev-log-info">完整 DEV 日誌可能含案件資料，僅限本機檢查。</span>' +
      '</div>';

    body.querySelector('[data-action="dev-log-export-file"]').addEventListener('click', devLogExportFile);
  }

  function devLogExportFile() {
    var project = global.JetStore.getState().project;
    if (!project) {
      Ui.setText('dev-log-info', '尚未開啟案件，無法輸出完整 DEV 日誌。');
      return;
    }
    Ui.run('輸出診斷日誌', function () {
      return global.JetApi.devLogExportFile({ projectId: project.projectId }).then(function (data) {
        Ui.setText('dev-log-info',
          '已輸出 ' + Number(data.lineCount).toLocaleString() + ' 筆：' + data.filePath +
          (data.source === 'ringBuffer' ? '（sink 檔不可讀，內容為 ring buffer 快照）' : ''));
      });
    });
  }

  Ui.initDevLogPanel = initDevLogPanel;
})(window);
