/**
 * steam.js — WebView2AppHost 汎用パススルー型 Steam ブリッジ
 *
 * Facepunch.Steamworks の C# API を JavaScript から直接呼び出せるブリッジ。
 * ES6 Proxy を用いて ClassName.MethodName を動的に捕捉し、
 * C# の汎用ディスパッチャへ JSON で転送する。
 *
 * 使い方:
 *   // Facepunch.Steamworks の公式ドキュメントのクラス名・メソッド名をそのまま指定
 *   await Steam.SteamUserStats.SetStat('NumWins', 10);
 *   await Steam.SteamUserStats.StoreStats();
 *
 *   const rank = await Steam.SteamUserStats.GetStatInt('NumWins');
 *
 *   // Steam イベントの受信
 *   Steam.on('OnAchievementProgress', ({ achievementName, currentProgress, maxProgress }) => {
 *     console.log(achievementName, currentProgress, '/', maxProgress);
 *   });
 *
 * インスタンスの利用:
 *   // 構造体やオブジェクトが返されると、自動的にプロキシが生成されます
 *   const board = await Steam.SteamUserStats.FindOrCreateLeaderboardAsync('Feet Traveled', 2, 1);
 *   await board.SubmitScoreAsync(5000);
 *
 * 注意:
 *   - WebView2 の画面上に Steam オーバーレイが重なるわけではない
 *   - showOverlay 系 API は Steam 側 UI を開く用途
 *   - Steam Deck / SteamOS は非対応（Windows 版 WebView2 専用）
 */
const Steam = (() => {
    'use strict';

    // WebView2 ホスト環境かどうかを判定
    const _isHost =
        typeof window !== 'undefined' &&
        typeof window.chrome !== 'undefined' &&
        typeof window.chrome.webview !== 'undefined';

    let _asyncId = 0;

    /** asyncId → { resolve, reject } の pending マップ */
    const _pending = new Map();

    /** イベント名 → ハンドラ関数リスト */
    const _eventHandlers = new Map();

    // ---------------------------------------------------------------------------
    // ハンドルの GC 通知（C# 側 _handleRegistry からの自動削除）
    // ---------------------------------------------------------------------------

    /**
     * JS プロキシが GC された際に C# へ release メッセージを送る。
     * FinalizationRegistry のコールバックは GC 後に非同期で呼ばれる（タイミング不保証）。
     * FinalizationRegistry が未サポートの環境（テスト用 Node.js 旧版など）では null になり、
     * 登録をスキップする（リークは起きるが動作は継続する）。
     */
    const _handleFinalizer = typeof FinalizationRegistry !== 'undefined'
        ? new FinalizationRegistry((handleId) => {
            if (!_isHost) return;
            window.chrome.webview.postMessage(JSON.stringify({
                source:    'steam',
                messageId: 'release',
                params:    { handleId },
            }));
        })
        : null;

    // ---------------------------------------------------------------------------
    // 再帰的にハンドルをプロキシに変換するヘルパー
    // ---------------------------------------------------------------------------
    function wrapHandles(val) {
        if (!val) return val;
        
        // 配列の場合は再帰的に適用
        if (Array.isArray(val)) {
            return val.map(wrapHandles);
        }
        
        if (typeof val === 'object' && val.__isHandle) {
            const proxy = new Proxy(Object.create(null), {
                get(_, methodName) {
                    if (typeof methodName !== 'string') return undefined;
                    if (methodName === 'then' || methodName === 'catch' || methodName === 'finally') return undefined;
                    // 標準JSメソッドのローカル処理（これらをC#に転送すると小文字のためMissingMethodExceptionになる）
                    if (methodName === 'toJSON') return () => ({ __isHandle: true, __handleId: val.__handleId, className: val.className });
                    if (methodName === 'toString') return () => `[Steam Proxy: ${val.className}]`;
                    if (methodName === 'valueOf') return () => val.__handleId;
                    return (...args) => invokeInstance(val.__handleId, methodName, args);
                }
            });
            // プロキシが GC されたら C# 側の _handleRegistry からも削除する。
            // 第2引数 val.__handleId が "held value" としてファイナライザに渡される。
            _handleFinalizer?.register(proxy, val.__handleId);
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

            if (!msg || msg.source !== 'steam') return;

            // invoke の応答
            if (msg.messageId === 'invoke-result') {
                const prom = _pending.get(msg.asyncId);
                if (!prom) return;
                _pending.delete(msg.asyncId);
                if (msg.error != null) {
                    prom.reject(new Error(`[Steam] ${msg.error}`));
                } else {
                    prom.resolve(wrapHandles(msg.result));
                }
                return;
            }

            // Steam コールバックイベント
            if (msg.event) {
                const handlers = _eventHandlers.get(msg.event);
                if (handlers) {
                    // イベントパラメータ内のハンドルもラップする
                    const wrappedParams = wrapHandles(msg.params);
                    handlers.forEach((h) => {
                        try { h(wrappedParams); } catch (err) {
                            console.error('[Steam] event handler error:', err);
                        }
                    });
                }
            }
        });
    }

    // ---------------------------------------------------------------------------
    // JS → C# 送信
    // ---------------------------------------------------------------------------

    /**
     * className と methodName を C# の汎用ディスパッチャへ転送する（静的呼び出し用）。
     * @param {string} className  Steamworks クラス名（例: "SteamUserStats"）
     * @param {string} methodName メソッド名
     * @param {any[]}  args       引数リスト
     * @returns {Promise<any>}    C# からの戻り値
     */
    function invoke(className, methodName, args) {
        return new Promise((resolve, reject) => {
            const id  = ++_asyncId;
            _pending.set(id, { resolve, reject });

            const message = JSON.stringify({
                source:    'steam',
                messageId: 'invoke',
                params:    { className, methodName, args: args ?? [] },
                asyncId:   id,
            });

            if (_isHost) {
                window.chrome.webview.postMessage(message);
            } else {
                // ホスト外（ブラウザ・テスト環境）: コンソールに出力してすぐ resolve
                console.log('[Steam Mock]', className + '.' + methodName, args);
                setTimeout(() => {
                    _pending.delete(id);
                    resolve(undefined);
                }, 4);
            }
        });
    }

    /**
     * handleId と methodName を C# の汎用ディスパッチャへ転送する（インスタンス呼び出し用）。
     */
    function invokeInstance(handleId, methodName, args) {
        return new Promise((resolve, reject) => {
            const id  = ++_asyncId;
            _pending.set(id, { resolve, reject });

            const message = JSON.stringify({
                source:    'steam',
                messageId: 'invoke',
                params:    { handleId, methodName, args: args ?? [] },
                asyncId:   id,
            });

            if (_isHost) {
                window.chrome.webview.postMessage(message);
            } else {
                console.log('[Steam Mock]', 'Instance(' + handleId + ').' + methodName, args);
                setTimeout(() => {
                    _pending.delete(id);
                    resolve(undefined);
                }, 4);
            }
        });
    }

    // ---------------------------------------------------------------------------
    // イベント登録 / 解除
    // ---------------------------------------------------------------------------

    /**
     * C# から発火する Steam コールバックイベントを受信するハンドラを登録する。
     * @param {string}   eventName  イベント名（例: "OnAchievementProgress"）
     * @param {Function} handler    コールバック関数（引数は params オブジェクト）
     */
    function on(eventName, handler) {
        if (!_eventHandlers.has(eventName)) _eventHandlers.set(eventName, []);
        _eventHandlers.get(eventName).push(handler);
    }

    /**
     * 登録済みハンドラを解除する。
     * @param {string}   eventName
     * @param {Function} handler
     */
    function off(eventName, handler) {
        const handlers = _eventHandlers.get(eventName);
        if (!handlers) return;
        _eventHandlers.set(eventName, handlers.filter((h) => h !== handler));
    }

    // ---------------------------------------------------------------------------
    // ES6 Proxy によるパススルーブリッジ
    // ---------------------------------------------------------------------------

    /**
     * Steam.SteamUserStats.SetAchievement("ACH") のように呼び出すと、
     * Proxy が className="SteamUserStats", methodName="SetAchievement", args=["ACH"] を
     * キャプチャして invoke() に渡す。
     */
    const _classProxyHandler = (className) => ({
        get(_, methodName) {
            if (typeof methodName !== 'string') return undefined;
            if (methodName === 'then' || methodName === 'catch' || methodName === 'finally') return undefined;
            // 標準JSメソッドのローカル処理
            if (methodName === 'toJSON') return () => ({ __isStaticClass: true, className });
            if (methodName === 'toString') return () => `[Steam Static Class: ${className}]`;
            if (methodName === 'valueOf') return () => className;
            return (...args) => invoke(className, methodName, args);
        },
    });

    const _rootProxy = new Proxy(Object.create(null), {
        get(_, prop) {
            if (typeof prop !== 'string') return undefined;
            if (prop === 'on')  return on;
            if (prop === 'off') return off;
            // Steamworks クラスのプロキシを返す
            return new Proxy(Object.create(null), _classProxyHandler(prop));
        },
    });

    return _rootProxy;
})();