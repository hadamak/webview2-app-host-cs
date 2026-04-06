/**
 * host.js — WebView2AppHost プラグインブリッジ
 *
 * WebView2 上の JS から、ホスト側に登録された任意のプラグインを呼び出すための
 * 3 階層プロキシブリッジ。プラグインの追加・変更に対して JS 側のコードは変更不要。
 *
 * 呼び出し構造:
 *   Host.<PluginName>.<ClassName>.<MethodName>(...args)  → Promise
 *
 *   PluginName  ホストに登録されたプラグインの識別名（C# 側の IHostPlugin.PluginName と対応）
 *   ClassName   プラグイン内のクラス名（プラグインの実装に依存）
 *   MethodName  呼び出すメソッド名
 *
 * 例（常駐の組み込み插件 Internal）:
 *   const preview = await Host.Browser.WebView.ScreenshotAsync();
 *
 * イベント受信:
 *   Host.on('EventName', (params) => { ... });
 *   Host.off('EventName', handler);
 *
 * メッセージフォーマット (JSON-RPC 2.0):
 *   リクエスト: { "jsonrpc": "2.0", "id": N, "method": "PluginName.ClassName.Method", "params": [...] }
 *   応答:       { "jsonrpc": "2.0", "id": N, "result": ... }
 *   エラー:     { "jsonrpc": "2.0", "id": N, "error": { "code": -32000, "message": "..." } }
 *   通知:       { "jsonrpc": "2.0", "method": "PluginName.EventName", "params": {...} }
 *
 * 注意:
 *   - await Host.<PluginName> が Thenable と誤認されないよう、then/catch/finally は
 *     プロキシの各階層で undefined を返す。
 *   - Windows 版 WebView2 専用。
 */
const Host = (() => {
    'use strict';

    // WebView2 ホスト環境かどうかを判定
    const _isHost =
        typeof window !== 'undefined' &&
        typeof window.chrome !== 'undefined' &&
        typeof window.chrome.webview !== 'undefined';

    let _requestId = 0;

    /** request id → { resolve, reject, pluginName } の pending マップ */
    const _pending = new Map();

    /** イベント名 → ハンドラ関数リスト */
    const _eventHandlers = new Map();

    // ---------------------------------------------------------------------------
    // ハンドルの GC 通知
    // ---------------------------------------------------------------------------

    /**
     * JS プロキシが GC された際に C# へ release メッセージを送る。
     * pluginName を含めることで複数插件環境に対応する。
     */
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

    // ---------------------------------------------------------------------------
    // ハンドルの再帰的ラップ
    // ---------------------------------------------------------------------------

    function wrapHandles(val, pluginName) {
        if (!val) return val;

        if (Array.isArray(val)) {
            return val.map(item => wrapHandles(item, pluginName));
        }

        if (typeof val === 'object' && val.__isHandle) {
            const proxy = new Proxy(Object.create(null), {
                get(_, methodName) {
                    if (typeof methodName !== 'string') return undefined;
                    if (methodName === 'then' || methodName === 'catch' || methodName === 'finally')
                        return undefined;
                    if (methodName === 'toJSON')
                        return () => ({ __isHandle: true, __handleId: val.__handleId, className: val.className });
                    if (methodName === 'toString')
                        return () => `[Host Plugin Proxy: ${val.className} (${pluginName})]`;
                    if (methodName === 'valueOf')
                        return () => val.__handleId;
                    return (...args) => invokeInstance(pluginName, val.__handleId, methodName, args);
                },
            });
            _handleFinalizer?.register(proxy, { handleId: val.__handleId, pluginName });
            return proxy;
        }

        return val;
    }

    // ---------------------------------------------------------------------------
    // C# → JS メッセージ受信
    // ---------------------------------------------------------------------------

    if (_isHost) {
        window.chrome.webview.addEventListener('message', (e) => {
            console.log('[host.js] received message:', e.data);
            let msg;
            try {
                msg = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
            } catch {
                console.log('[host.js] parse failed');
                return;
            }

            console.log('[host.js] parsed:', msg);

            // JSON-RPC 2.0 バージョン確認
            if (!msg || msg.jsonrpc !== '2.0') {
                console.log('[host.js] not JSON-RPC 2.0, ignoring');
                return;
            }

            // 応答（id あり）
            if (msg.id != null) {
                const prom = _pending.get(msg.id);
                if (!prom) return;
                _pending.delete(msg.id);
                if (msg.error) {
                    prom.reject(new Error(`[Host/${prom.pluginName}] ${msg.error.message}`));
                } else {
                    prom.resolve(wrapHandles(msg.result, prom.pluginName));
                }
                return;
            }

            // 通知（id なし）: イベント通知
            if (msg.method) {
                const dotIdx = msg.method.indexOf('.');
                if (dotIdx === -1) return;
                const pluginName = msg.method.slice(0, dotIdx);
                const eventName = msg.method.slice(dotIdx + 1);
                const handlers = _eventHandlers.get(eventName);
                if (handlers) {
                    const wrappedParams = wrapHandles(msg.params, pluginName);
                    handlers.forEach((h) => {
                        try { h(wrappedParams); } catch (err) {
                            console.error('[Host] event handler error:', err);
                        }
                    });
                }
            }
        });
    }

    // ---------------------------------------------------------------------------
    // JS → C# 送信（静的メソッド呼び出し）
    // ---------------------------------------------------------------------------

    /**
     * @param {string} pluginName  プラグイン名（例: "Steam"）
     * @param {string} className   クラス名（例: "SteamUserStats"）
     * @param {string} methodName  メソッド名（例: "SetStat"）
     * @param {any[]}  args        引数リスト
     * @returns {Promise<any>}
     */
    function invoke(pluginName, className, methodName, args) {
        console.log(`[host.js] invoke: ${pluginName}.${className}.${methodName}`, args);
        return new Promise((resolve, reject) => {
            const id = ++_requestId;
            _pending.set(id, { resolve, reject, pluginName });

            const message = JSON.stringify({
                jsonrpc: '2.0',
                id,
                method: `${pluginName}.${className}.${methodName}`,
                params: args ?? [],
            });

            console.log(`[host.js] posting: ${message}`);
            if (_isHost) {
                window.chrome.webview.postMessage(message);
            } else {
                // ホスト外（ブラウザ・テスト環境）: コンソールに出力して即 resolve
                console.log(`[Host Mock] ${pluginName}.${className}.${methodName}`, args);
                setTimeout(() => { _pending.delete(id); resolve(undefined); }, 4);
            }
        });
    }

    /**
     * インスタンスメソッド呼び出し（handleId 経由）。
     * @param {string} pluginName  ハンドルが属するプラグイン名
     * @param {number} handleId    C# 側の _handleRegistry キー
     * @param {string} methodName  メソッド名
     * @param {any[]}  args        引数リスト
     */
    function invokeInstance(pluginName, handleId, methodName, args) {
        return new Promise((resolve, reject) => {
            const id = ++_requestId;
            _pending.set(id, { resolve, reject, pluginName });

            const message = JSON.stringify({
                jsonrpc: '2.0',
                id,
                method: `${pluginName}.${methodName}`,
                params: { handleId, args: args ?? [] },
            });

            if (_isHost) {
                window.chrome.webview.postMessage(message);
            } else {
                console.log(`[Host Mock] Instance(${pluginName}:${handleId}).${methodName}`, args);
                setTimeout(() => { _pending.delete(id); resolve(undefined); }, 4);
            }
        });
    }

    // ---------------------------------------------------------------------------
    // イベント登録 / 解除
    // ---------------------------------------------------------------------------

    function on(eventName, handler) {
        if (!_eventHandlers.has(eventName)) _eventHandlers.set(eventName, []);
        _eventHandlers.get(eventName).push(handler);
    }

    function off(eventName, handler) {
        const handlers = _eventHandlers.get(eventName);
        if (!handlers) return;
        _eventHandlers.set(eventName, handlers.filter((h) => h !== handler));
    }

    // ---------------------------------------------------------------------------
    // 3 階層プロキシ: Host.PluginName.ClassName.methodName(args)
    // ---------------------------------------------------------------------------

    const _methodProxyHandler = (pluginName, className) => ({
        get(_, methodName) {
            if (typeof methodName !== 'string') return undefined;
            if (methodName === 'then' || methodName === 'catch' || methodName === 'finally')
                return undefined;
            if (methodName === 'toJSON')
                return () => ({ __isStaticClass: true, pluginName, className });
            if (methodName === 'toString')
                return () => `[Host Static: ${pluginName}.${className}]`;
            if (methodName === 'valueOf')
                return () => `${pluginName}.${className}`;
            return (...args) => invoke(pluginName, className, methodName, args);
        },
    });

    const _classProxyHandler = (pluginName) => ({
        get(_, className) {
            if (typeof className !== 'string') return undefined;
            // await Host.Steam が Thenable と誤認されないよう undefined を返す
            if (className === 'then' || className === 'catch' || className === 'finally')
                return undefined;
            return new Proxy(Object.create(null), _methodProxyHandler(pluginName, className));
        },
    });

    const _rootProxy = new Proxy(Object.create(null), {
        get(_, prop) {
            if (typeof prop !== 'string') return undefined;
            if (prop === 'on')  return on;
            if (prop === 'off') return off;
            return new Proxy(Object.create(null), _classProxyHandler(prop));
        },
    });

    return _rootProxy;
})();