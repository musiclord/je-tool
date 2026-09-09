/*
  WebView2 bridge boundary.
  This file is reserved for the single JetApi action channel.
*/
(function (global) {
  'use strict';

  var pending = Object.create(null);
  var requestStartedHandlers = [];

  function isReady() {
    return !!(global.chrome && global.chrome.webview && global.chrome.webview.postMessage);
  }

  function invoke(action, payload) {
    if (!isReady()) {
      return Promise.reject(new Error('JET host bridge is not available.'));
    }

    var requestId = createRequestId();
    var request = {
      requestId: requestId,
      action: action,
      payload: payload || {}
    };

    var promise = new Promise(function (resolve, reject) {
      pending[requestId] = {
        resolve: resolve,
        reject: reject
      };

      requestStartedHandlers.slice().forEach(function (handler) {
        try {
          handler({ requestId: requestId, action: action });
        } catch (error) {
          // request 追蹤只服務取消 UX；訂閱者失敗不得阻斷真正的 bridge request。
          if (global.console) { global.console.error('JET request-start handler failed:', error); }
        }
      });

      global.chrome.webview.postMessage(request);
    });

    // Ui.run 只保存 requestId 供 operation.cancel 使用；不暴露 transport pending table。
    promise.requestId = requestId;
    return promise;
  }

  function createRequestId() {
    if (global.crypto && typeof global.crypto.randomUUID === 'function') {
      return global.crypto.randomUUID();
    }

    return 'jet-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2);
  }

  // host→web 事件訂閱表（manifest「Host→Web 事件」：信封 { event, data }、無 requestId）
  var eventHandlers = Object.create(null);

  function receive(event) {
    var message = event.data;
    if (typeof message === 'string') {
      try {
        message = JSON.parse(message);
      } catch (error) {
        return;
      }
    }

    if (!message) {
      return;
    }

    if (message.event) {
      var handlers = eventHandlers[message.event];
      if (handlers) {
        handlers.slice().forEach(function (handler) {
          try {
            handler(message.data);
          } catch (error) {
            // 單一訂閱者出錯不得打斷其他訂閱者；事件是 UX 提示，不承載權威
            if (global.console) { global.console.error('JET event handler failed:', error); }
          }
        });
      }
      return;
    }

    if (!message.requestId || !pending[message.requestId]) {
      return;
    }

    var callbacks = pending[message.requestId];
    delete pending[message.requestId];

    if (message.ok) {
      callbacks.resolve(message.data);
      return;
    }

    var detail = message.error && message.error.message
      ? message.error.message
      : 'JET bridge request failed.';

    // 把 wire 錯誤碼掛到 Error 上，供 Ui.run 區分（如 operation_in_progress → 「請稍候」）。
    var err = new Error(detail);
    err.code = message.error && message.error.code ? message.error.code : null;
    err.correlationId = typeof message.correlationId === 'string' ? message.correlationId : null;
    err.field = message.error && typeof message.error.field === 'string'
      ? message.error.field
      : null;
    // invalid_scenario 這類可歸屬到條件列的錯誤帶 details（group、rule、message）；其他錯誤為 null。
    err.details = message.error && Array.isArray(message.error.details) ? message.error.details : null;
    callbacks.reject(err);
  }

  // WebView 卸載後 host 不可能再回 response；逐筆刪除並 reject，避免 pending Promise 永久懸掛。
  // bridge_unloaded 是純前端 transport 狀態，不是 wire error code。
  function rejectAllPending() {
    Object.keys(pending).forEach(function (requestId) {
      var callbacks = pending[requestId];
      delete pending[requestId];
      var error = new Error('JET host bridge was unloaded before the request completed.');
      error.code = 'bridge_unloaded';
      callbacks.reject(error);
    });
  }

  if (isReady() && typeof global.chrome.webview.addEventListener === 'function') {
    global.chrome.webview.addEventListener('message', receive);
  }
  if (typeof global.addEventListener === 'function') {
    global.addEventListener('pagehide', rejectAllPending);
  }

  // 正式契約 actions（docs/action-contract-manifest.md 為 source of truth）。
  // 新增 action：先改 manifest，再加進這份清單，最後才能在 UI 呼叫 JetApi.<method>。
  var SUPPORTED_ACTIONS = [
    'system.ping',
    'operation.cancel',
    'system.databaseInfo',
    'system.whoAmI',
    'project.listLocal',
    'project.list',
    'project.create',
    'project.load',
    'project.delete',
    'project.saveProgress',
    'project.heartbeat',
    'project.releaseLock',
    'project.loadDemo',
    'demo.exportGlFile',
    'demo.exportTbFile',
    'demo.exportAccountMappingFile',
    'demo.exportAuthorizedPreparerFile',
    'import.gl.fromFile',
    'import.tb.fromFile',
    'import.accountMapping.fromFile',
    'accountTaxonomy.save',
    'import.authorizedPreparer.fromFile',
    'import.inspectFile',
    'import.previewFile',
    'import.holiday',
    'import.makeupDay',
    'import.holiday.fromFile',
    'import.makeupDay.fromFile',
    'calendar.setNonWorkingDays',
    'mapping.autoSuggest',
    'mapping.valueProfile',
    'mapping.restoreDraft',
    'mapping.commit.gl',
    'mapping.commit.tb',
    'validate.run',
    'prescreen.run',
    'filter.preview',
    'filter.commit',
    'query.dataPreview',
    'query.completenessDiffPage',
    'query.docBalancePage',
    'query.nullRecordsPage',
    'query.sourceQualityPage',
    'query.filterHitsPage',
    'query.filterVoucherPage',
    'query.filterVoucherRowsPage',
    'query.accountMappingBlankPage',
    'query.prescreenPage',
    'query.infSamplePage',
    'query.tagMatrixScenarios',
    'query.tagMatrixVoucherPage',
    'query.tagMatrixRowPage',
    'export.validationArtifacts',
    'export.prescreenReport',
    'export.criteriaSelectionReport',
    'export.workpaperStream',
    'export.accountMappingTemplate',
    'log.append',
    'log.recent',
    'support.log.export',
    'host.selectFile',
    'host.selectFiles',
    'host.selectSavePath',
    'host.openFolder',
    'host.exitApp',
    'dev.db.overview',
    'dev.db.tableData',
    'dev.db.reconcile',
    'dev.log.export',
    'dev.log.exportFile'
  ];

  // action name → lowerCamelCase method：第一段保留小寫，後續段首字母大寫。
  // 例：mapping.commit.gl → mappingCommitGl
  function toMethodName(action) {
    var parts = action.split('.');
    var name = parts[0];
    for (var i = 1; i < parts.length; i++) {
      name += parts[i].charAt(0).toUpperCase() + parts[i].slice(1);
    }
    return name;
  }

  // host→web 事件訂閱（manifest「Host→Web 事件」）。handler 收到 data 物件。
  function on(eventName, handler) {
    (eventHandlers[eventName] = eventHandlers[eventName] || []).push(handler);
  }

  function off(eventName, handler) {
    var handlers = eventHandlers[eventName];
    if (!handlers) { return; }
    var index = handlers.indexOf(handler);
    if (index >= 0) { handlers.splice(index, 1); }
  }

  // UI 可用這個 transport-neutral 通知追蹤真正送出的 request。它也能涵蓋
  // JetApi promise 已經接過 .then()、requestId 屬性不再位於最外層 promise 的流程。
  function onRequestStarted(handler) {
    requestStartedHandlers.push(handler);
  }

  var JetApi = {
    isReady: isReady,
    invoke: invoke,
    on: on,
    off: off,
    onRequestStarted: onRequestStarted
  };

  SUPPORTED_ACTIONS.forEach(function (action) {
    JetApi[toMethodName(action)] = function (payload) {
      return invoke(action, payload);
    };
  });

  global.JetApi = JetApi;
})(window);
