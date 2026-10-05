(function () {
  'use strict';
  // 只由瀏覽器開發主機注入，排在正式 jet-api.js 之前；正式 WebView2 不載入此檔。
  // action 經同源 POST 交給正式 ActionDispatcher，host→web 事件改走 Server-Sent Events。
  var listeners = [];

  function deliver(message) {
    listeners.slice().forEach(function (listener) {
      try {
        listener({ data: message });
      } catch (error) {
        console.error('JET browser bridge listener failed:', error);
      }
    });
  }

  window.chrome = window.chrome || {};
  window.chrome.webview = {
    addEventListener: function (name, listener) {
      if (name === 'message') { listeners.push(listener); }
    },
    removeEventListener: function (name, listener) {
      var index = listeners.indexOf(listener);
      if (name === 'message' && index >= 0) { listeners.splice(index, 1); }
    },
    postMessage: function (request) {
      fetch('/__jet/action', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request)
      })
        .then(function (response) {
          if (!response.ok) { throw new Error('HTTP ' + response.status); }
          return response.json();
        })
        .then(deliver)
        .catch(function (error) {
          deliver({
            requestId: request && request.requestId,
            ok: false,
            data: null,
            error: { code: 'bridge_error', message: '無法連到瀏覽器開發主機：' + error.message },
            correlationId: null
          });
        });
    }
  };

  var events = new EventSource('/__jet/events');
  events.onmessage = function (event) {
    try {
      deliver(JSON.parse(event.data));
    } catch (error) {
      console.error('JET browser bridge event failed:', error);
    }
  };
  window.JetBrowserHost = { events: events };
})();
