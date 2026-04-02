/**
 * host.js — WebView2AppHost ユニバーサル・プラグイン・アーキテクチャ (UPA) ブリッジ
 *
 * steam.js を拡張し、プラグイン名を第一階層に持つ 3 階層プロキシ。
 *
 * 使い方:
 *   // Steam プラグイン（Host.Steam.ClassName.MethodName）
 *   await Host.Steam.SteamUserStats.SetStat('NumGames', 10);
 *   await Host.Steam.SteamUserStats.StoreStats();
 *
 *   // 将来の Node.js プラグイン（同じ構造で透過的に追加可能）
 *   // const result = await Host.Node.ImageProcessor.resize('photo.jpg', 300);
 *
 *   // イベント受信（ Host.on/off でどのプラグインのイベントも受け取れる）
 *   Host.on('OnAchievementProgress', ({ achievementName, currentProgress, maxProgress }) => {
 *     console.log(achievementName, currentProgress, '/', maxProgress);
 *   });
 *
 * 後方互換:
 *   このファイルは Steam グローバル変数も提供する。
 *   steam.js の代わりに host.js を読み込めばそのまま既存コードが動作する。
 *
 *   // steam.js で書いた既存コードはそのまま動く
 *   await Steam.SteamUserStats.SetStat('NumGames', 10);
 *   Steam.on('OnGameOverlayActivated', handler);
 *
 * メッセージフォーマット (JS → C#):
 *   { "source": "<PluginName>", "messageId": "invoke",
 *     "params": { "className": "...", "methodName": "...", "args": [...] },
 *     "asyncId": N }
 *
 *   PluginName は Proxy アクセス時のプロパティ名をそのまま使う（例: "Steam"）。
 *   C# 側の SteamBridgeImpl は大文字小文字を区別せず受け付ける。
 *
 * 注意:
 *   - host.js と steam.js を同一ページに両方読み込まないこと（メッセージ二重処理が起きる）。
 *   - WebView2 の画面上に Steam オーバーレイが重なるわけではない。
 *   - Steam Deck / SteamOS は非対応（Windows 版 WebView2 専用）。
 */
const Host = (() => {
    'use strict';

    // WebView2 ホスト環境かどうかを判定
    const _isHost =
        typeof window !== 'undefined' &&
        typeof window.chrome !== 'undefined' &&
        typeof window.chrome.webview !== 'undefined';

    let _asyncId = 0;

    /** asyncId → { resolve, reject, pluginName } の pending マップ */
    const _pending = new Map();

    /** イベント名 → ハンドラ関数リスト */
    const _eventHandlers = new Map();

    // ---------------------------------------------------------------------------
    // ハンドルの GC 通知
    // ---------------------------------------------------------------------------

    /**
     * JS プロキシが GC された際に C# へ release メッセージを送る。
     * pluginName を含めることで将来の複数プラグイン環境に備える。
     */
    const _handleFinalizer = typeof FinalizationRegistry !== 'undefined'
        ? new FinalizationRegistry(({ handleId, pluginName }) => {
            if (!_isHost) return;
            window.chrome.webview.postMessage(JSON.stringify({
                source:    pluginName,
                messageId: 'release',
                params:    { handleId },
            }));
        })
        : null;

    // WebView2 メッセージ受信イベントリスナー
    if (_isHost) {
        window.chrome.webview.addEventListener('message', (e) => {
            try {
                const msg = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
                if (msg && msg.source && msg.messageId === 'invoke-result') {
                    const pending = _pending.get(msg.asyncId);
                    if (pending) {
                        _pending.delete(msg.asyncId);
                        if (msg.error) {
                            pending.reject(new Error(msg.error));
                        } else {
                            pending.resolve(msg.result);
                        }
                    }
                }
            } catch (err) {
                console.error('[Host] message parse error:', err);
            }
        });
    }

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
            let msg;
            try {
                msg = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
            } catch {
                return;
            }

            // source フィールドのないメッセージはシステムメッセージ（visibilityChange 等）のため無視
            if (!msg || !msg.source) return;

            // invoke の応答（asyncId でルーティング）
            if (msg.messageId === 'invoke-result') {
                const prom = _pending.get(msg.asyncId);
                if (!prom) return;
                _pending.delete(msg.asyncId);
                if (msg.error != null) {
                    prom.reject(new Error(`[Host/${prom.pluginName}] ${msg.error}`));
                } else {
                    prom.resolve(wrapHandles(msg.result, prom.pluginName));
                }
                return;
            }

            // プラグインからのイベント通知
            if (msg.event) {
                const handlers = _eventHandlers.get(msg.event);
                if (handlers) {
                    const wrappedParams = wrapHandles(msg.params, msg.source);
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
        return new Promise((resolve, reject) => {
            const id = ++_asyncId;
            _pending.set(id, { resolve, reject, pluginName });

            const message = JSON.stringify({
                source:    pluginName,
                messageId: 'invoke',
                params:    { className, methodName, args: args ?? [] },
                asyncId:   id,
            });

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
            const id = ++_asyncId;
            _pending.set(id, { resolve, reject, pluginName });

            const message = JSON.stringify({
                source:    pluginName,
                messageId: 'invoke',
                params:    { handleId, methodName, args: args ?? [] },
                asyncId:   id,
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
            // Host.on / Host.off はイベント登録用
            if (prop === 'on')  return on;
            if (prop === 'off') return off;
            // Host.PluginName → クラス階層プロキシを返す
            return new Proxy(Object.create(null), _classProxyHandler(prop));
        },
    });

    return _rootProxy;
})();

// ---------------------------------------------------------------------------
// 後方互換: Steam グローバル変数
//
// steam.js の代わりに host.js を読み込んだ場合でも
// 既存の Steam.ClassName.MethodName(...) と Steam.on/off(...) が動作する。
//
// 新規コードでは Host.Steam.ClassName.MethodName(...) を推奨する。
// ---------------------------------------------------------------------------

/* global Steam */
const Steam = new Proxy(Object.create(null), {
    get(_, prop) {
        if (typeof prop !== 'string') return undefined;
        // Steam.on / Steam.off → Host のイベントシステムへ委譲
        if (prop === 'on')  return Host.on;
        if (prop === 'off') return Host.off;
        // Steam.ClassName.method → Host.Steam.ClassName.method
        return Host.Steam[prop];
    },
});
