/**
 * host.js — WebView2AppHost プラグインブリッジ
 *
 * WebView2 上の JS から、ホスト側に登録された任意のプラグインを呼び出すための
 * 3 階層プロキシブリッジ。プラグインの追加・変更に対して JS 側のコードは変更不要。
 *
 * 呼び出し構造:
 *   Host.<PluginName>.<ClassName>.<MethodName>(...args)  → Promise
 *
 *   PluginName  ホストに登録されたプラグインの識別名（alias）
 *   ClassName   プラグイン内のクラス名
 *   MethodName  呼び出すメソッド名
 */
const Host = (() => {
    'use strict';

    const _isHost =
        typeof window !== 'undefined' &&
        typeof window.chrome !== 'undefined' &&
        typeof window.chrome.webview !== 'undefined';

    let _requestId = 0;
    const _pending = new Map();
    const _eventHandlers = new Map();

    /** JS プロキシが GC された際に C# へ release メッセージを送る */
    const _handleFinalizer = typeof FinalizationRegistry !== 'undefined'
        ? new FinalizationRegistry(({ handleId, pluginName }) => {
            if (!_isHost) return;
            window.chrome.webview.postMessage(JSON.stringify({
                jsonrpc: '2.0',
                method: `${pluginName}.release`,
                params: { handleId },
            }));
        })
        : null;

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

            // 応答
            if (msg.id != null) {
                const prom = _pending.get(msg.id);
                if (!prom) return;
                _pending.delete(msg.id);
                if (msg.error) prom.reject(new Error(`[Host/${prom.pluginName}] ${msg.error.message}`));
                else prom.resolve(wrapHandles(msg.result, prom.pluginName));
                return;
            }

            // 通知（イベント）
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

    function invoke(pluginName, className, methodName, args) {
        return new Promise((resolve, reject) => {
            const id = ++_requestId;
            _pending.set(id, { resolve, reject, pluginName });
            const message = JSON.stringify({ jsonrpc: '2.0', id, method: `${pluginName}.${className}.${methodName}`, params: args ?? [] });
            if (_isHost) window.chrome.webview.postMessage(message);
            else { console.log(`[Host Mock] ${pluginName}.${className}.${methodName}`, args); setTimeout(() => { _pending.delete(id); resolve(undefined); }, 4); }
        });
    }

    function invokeInstance(pluginName, handleId, methodName, args) {
        return new Promise((resolve, reject) => {
            const id = ++_requestId;
            _pending.set(id, { resolve, reject, pluginName });
            const message = JSON.stringify({ jsonrpc: '2.0', id, method: `${pluginName}.${methodName}`, params: { handleId, args: args ?? [] } });
            if (_isHost) window.chrome.webview.postMessage(message);
            else { console.log(`[Host Mock] Instance(${pluginName}:${handleId}).${methodName}`, args); setTimeout(() => { _pending.delete(id); resolve(undefined); }, 4); }
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

    const _rootProxy = new Proxy(Object.create(null), {
        get(_, prop) {
            if (typeof prop !== 'string') return undefined;
            if (prop === 'on') return (name, h) => { if (!_eventHandlers.has(name)) _eventHandlers.set(name, []); _eventHandlers.get(name).push(h); };
            if (prop === 'off') return (name, h) => { const hs = _eventHandlers.get(name); if (hs) _eventHandlers.set(name, hs.filter(x => x !== h)); };
            if (prop === 'release') return (obj) => { if (obj && obj.valueOf) { const id = obj.valueOf(); _handleFinalizer?.cleanup(); /* 手動解放は未実装だが形式として用意 */ } };
            return new Proxy(Object.create(null), _classProxyHandler(prop));
        },
    });

    return _rootProxy;
})();

/**
 * 後方互換 Steam プロキシ。
 * triggerScreenshot は Host.Internal と Host.Steam を組み合わせて JS 側で実現する。
 */
const Steam = new Proxy(Object.create(null), {
    get(_, prop) {
        if (typeof prop !== 'string') return undefined;
        if (prop === 'on' || prop === 'off') return Host[prop];
        
        if (prop === 'SteamScreenshots') {
            return new Proxy(Object.create(null), {
                get(_, method) {
                    if (method === 'TriggerScreenshot') {
                        return async () => {
                            const preview = await Host.Internal.Host.CapturePreviewAsync();
                            return await Host.Steam.SteamScreenshots.WriteScreenshot(preview.rgb, preview.width, preview.height);
                        };
                    }
                    return Host.Steam.SteamScreenshots[method];
                }
            });
        }
        return Host.Steam[prop];
    },
});
