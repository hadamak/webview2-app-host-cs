const Host = (() => {
    'use strict';

    const _isHost =
        typeof window !== 'undefined' &&
        typeof window.chrome !== 'undefined' &&
        typeof window.chrome.webview !== 'undefined';

    let _requestId = 0;
    const _pending = new Map();
    const _eventHandlers = new Map();
    const _diagHandlers = new Set();

    const _handleFinalizer = typeof FinalizationRegistry !== 'undefined'
        ? new FinalizationRegistry(({ handleId, pluginName }) => {
            if (!_isHost) return;
            const payload = {
                jsonrpc: '2.0',
                method: `${pluginName}.release`,
                params: { handleId },
            };
            _emitDiagnostic('outbound', payload);
            window.chrome.webview.postMessage(JSON.stringify(payload));
        })
        : null;

    function _emitDiagnostic(direction, payload) {
        const event = {
            direction,
            payload,
            timestamp: new Date().toISOString(),
        };
        _diagHandlers.forEach((handler) => {
            try { handler(event); } catch (err) { console.error('[Host.Diagnostics]', err); }
        });
    }

    function wrapHandles(val, pluginName) {
        if (!val) return val;
        if (Array.isArray(val)) return val.map(item => wrapHandles(item, pluginName));
        if (typeof val === 'object' && val.__isHandle) {
            const proxy = new Proxy(Object.create(null), {
                get(_, methodName) {
                    if (typeof methodName !== 'string') return undefined;
                    if (methodName === 'then' || methodName === 'catch' || methodName === 'finally') return undefined;
                    if (methodName === 'toJSON') return () => ({ __isHandle: true, __handleId: val.__handleId, className: val.className });
                    if (methodName === 'toString') return () => `[Host Plugin Proxy: ${val.className} (${pluginName})]`;
                    if (methodName === 'valueOf') return () => val.__handleId;
                    return (...args) => invokeInstance(pluginName, val.__handleId, methodName, args);
                },
            });
            _handleFinalizer?.register(proxy, { handleId: val.__handleId, pluginName });
            return proxy;
        }
        return val;
    }

    if (_isHost) {
        window.chrome.webview.addEventListener('message', (e) => {
            let msg;
            try { msg = typeof e.data === 'string' ? JSON.parse(e.data) : e.data; } catch { return; }
            if (!msg || msg.jsonrpc !== '2.0') return;

            _emitDiagnostic('inbound', msg);

            if (msg.id != null) {
                const prom = _pending.get(msg.id);
                if (!prom) return;
                _pending.delete(msg.id);
                if (msg.error) prom.reject(new Error(`[Host/${prom.pluginName}] ${msg.error.message}`));
                else prom.resolve(wrapHandles(msg.result, prom.pluginName));
                return;
            }

            if (msg.method) {
                const dotIdx = msg.method.indexOf('.');
                if (dotIdx === -1) return;
                const pluginName = msg.method.slice(0, dotIdx);
                const eventName = msg.method.slice(dotIdx + 1);
                const handlers = _eventHandlers.get(eventName);
                if (handlers) {
                    const wrappedParams = wrapHandles(msg.params, pluginName);
                    handlers.forEach((h) => { try { h(wrappedParams); } catch (err) { console.error('[Host] event error:', err); } });
                }
            }
        });
    }

    function sendMessage(message, pluginName, resolve, reject) {
        const id = ++_requestId;
        _pending.set(id, { resolve, reject, pluginName });
        _emitDiagnostic('outbound', message);

        if (_isHost) {
            window.chrome.webview.postMessage(JSON.stringify(message));
        } else {
            console.log(`[Host Mock] ${message.method}`, message.params);
            setTimeout(() => {
                _pending.delete(id);
                resolve(undefined);
            }, 4);
        }
    }

    function invoke(pluginName, className, methodName, args) {
        return new Promise((resolve, reject) => {
            const id = _requestId + 1;
            const message = { jsonrpc: '2.0', id, method: `${pluginName}.${className}.${methodName}`, params: args ?? [] };
            sendMessage(message, pluginName, resolve, reject);
        });
    }

    function invokeInstance(pluginName, handleId, methodName, args) {
        return new Promise((resolve, reject) => {
            const id = _requestId + 1;
            const message = { jsonrpc: '2.0', id, method: `${pluginName}.${methodName}`, params: { handleId, args: args ?? [] } };
            sendMessage(message, pluginName, resolve, reject);
        });
    }

    const _methodProxyHandler = (pluginName, className) => ({
        get(_, methodName) {
            if (typeof methodName !== 'string') return undefined;
            if (methodName === 'then' || methodName === 'catch' || methodName === 'finally') return undefined;
            return (...args) => invoke(pluginName, className, methodName, args);
        },
    });

    const _classProxyHandler = (pluginName) => ({
        get(_, className) {
            if (typeof className !== 'string' || className === 'then' || className === 'catch' || className === 'finally') return undefined;
            return new Proxy(Object.create(null), _methodProxyHandler(pluginName, className));
        },
    });

    const diagnosticsApi = Object.freeze({
        isHost: () => _isHost,
        onMessage(handler) {
            if (typeof handler !== 'function') return () => {};
            _diagHandlers.add(handler);
            return () => _diagHandlers.delete(handler);
        },
    });

    const _rootProxy = new Proxy(Object.create(null), {
        get(_, prop) {
            if (typeof prop !== 'string') return undefined;
            if (prop === 'on') return (name, h) => { if (!_eventHandlers.has(name)) _eventHandlers.set(name, []); _eventHandlers.get(name).push(h); };
            if (prop === 'off') return (name, h) => { const hs = _eventHandlers.get(name); if (hs) _eventHandlers.set(name, hs.filter(x => x !== h)); };
            if (prop === 'release') return () => {};
            if (prop === 'diagnostics') return diagnosticsApi;
            return new Proxy(Object.create(null), _classProxyHandler(prop));
        },
    });

    return _rootProxy;
})();

if (typeof window !== 'undefined') {
    window.Host = Host;
}
