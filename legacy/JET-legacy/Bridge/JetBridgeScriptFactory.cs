using System.Text.Json;

namespace JET.Bridge
{
    public static class JetBridgeScriptFactory
    {
        public static string Create(IReadOnlyCollection<string> supportedActions)
        {
            var actionsJson = JsonSerializer.Serialize(supportedActions.OrderBy(static action => action).ToArray());

            return $$"""
(function () {
    if (!window.chrome || !window.chrome.webview) {
        return;
    }

    if (window.jet) {
        return;
    }

    const supportedActions = Object.freeze({{actionsJson}});
    const pending = new Map();

    function createRequestId() {
        if (window.crypto && typeof window.crypto.randomUUID === 'function') {
            return window.crypto.randomUUID();
        }

        return `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    }

    window.jet = Object.freeze({
        supportedActions,
        invoke(action, payload) {
            return new Promise((resolve, reject) => {
                const requestId = createRequestId();
                pending.set(requestId, { resolve, reject });

                window.chrome.webview.postMessage({
                    requestId,
                    action,
                    payload: payload ?? {}
                });
            });
        }
    });

    function toFacadeMethodName(action) {
        const segments = String(action).split('.');
        return segments
            .map((seg, idx) => {
                if (seg.length === 0) return seg;
                if (idx === 0) return seg;
                return seg.charAt(0).toUpperCase() + seg.slice(1);
            })
            .join('');
    }

    const facade = Object.create(null);
    for (const action of supportedActions) {
        const methodName = toFacadeMethodName(action);
        facade[methodName] = function (payload) {
            return window.jet.invoke(action, payload);
        };
    }
    facade.__unknown = function (methodName) {
        return Promise.reject(new Error(
            'JetApi method "' + methodName + '" not found. ' +
            'Add action to docs/action-contract-manifest.md first.'
        ));
    };

    window.JetApi = new Proxy(Object.freeze(facade), {
        get(target, prop) {
            if (prop in target) return target[prop];
            if (typeof prop === 'symbol') return undefined;
            return function () { return target.__unknown(prop); };
        }
    });

    window.chrome.webview.addEventListener('message', event => {
        const message = event.data;
        if (!message || !message.requestId) {
            return;
        }

        const pendingRequest = pending.get(message.requestId);
        if (!pendingRequest) {
            return;
        }

        pending.delete(message.requestId);

        if (message.ok) {
            pendingRequest.resolve(message.data ?? null);
            return;
        }

        const errorMessage = message.error && message.error.message
            ? message.error.message
            : 'Unknown bridge error.';

        pendingRequest.reject(new Error(errorMessage));
    });

    window.addEventListener('DOMContentLoaded', async () => {
        try {
            const bootstrap = await window.jet.invoke('app.bootstrap', {});
            window.__JET_BOOTSTRAP__ = bootstrap;

            const statusBadge = document.getElementById('statusBadge');
            if (statusBadge && bootstrap && bootstrap.database && bootstrap.database.provider) {
                statusBadge.textContent = `Shell Ready / ${bootstrap.database.provider}`;
            }

            console.info('JET bootstrap', bootstrap);
        }
        catch (error) {
            console.error('JET bootstrap failed', error);
        }

        window.dispatchEvent(new CustomEvent('jet:bridge-ready', {
            detail: { supportedActions, bootstrap: window.__JET_BOOTSTRAP__ }
        }));
    }, { once: true });
})();
""";
        }
    }
}
