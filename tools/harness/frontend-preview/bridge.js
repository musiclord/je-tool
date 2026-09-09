(function () {
  'use strict';
  const listeners = [];
  const requests = [];
  const ready = fetch('/fixtures.json').then(response => {
    if (!response.ok) throw new Error('合成資料已失效或尚未建立，請依開發指南重建。');
    return response.json();
  });
  // Install only in the generated preview, before the unchanged production JetApi.
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    addEventListener: function (name, listener) { if (name === 'message') listeners.push(listener); },
    postMessage: function (request) {
      requests.push(structuredClone(request));
      if (requests.length > 50) requests.shift();
      ready.then(bundle => window.JetPreviewReplay.dispatch(bundle, request.action, request.payload))
        .then(data => deliver({ requestId: request.requestId, ok: true, data, error: null, correlationId: 'synthetic-preview' }))
        .catch(error => deliver({ requestId: request.requestId, ok: false, data: null,
          error: { code: 'preview_not_supported', message: error.message }, correlationId: 'synthetic-preview' }));
    }
  };
  function deliver(message) { listeners.forEach(listener => listener({ data: message })); }
  window.JetPreview = { ready, requests };
})();
