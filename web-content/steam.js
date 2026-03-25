/**
 * steam.js — WebView2AppHost 汎用パススルー型 Steam ブリッジ
 *
 * Facepunch.Steamworks の C# API を JavaScript から直接呼び出せるブリッジ。
 * ES6 Proxy を用いて ClassName.MethodName を動的に捕捉し、
 * C# の汎用ディスパッチャへ JSON で転送する。
 *
 * 使い方:
 *   // Facepunch.Steamworks の公式ドキュメントのクラス名・メソッド名をそのまま指定
 *   await Steam.SteamUserStats.SetAchievement('ACH_WIN_ONE_GAME');
 *   await Steam.SteamUserStats.StoreStats();
 *
 *   const rank = await Steam.SteamUserStats.GetStatInt('NumWins');
 *
 *   // Steam イベントの受信
 *   Steam.on('OnAchievementProgress', ({ achievementName, currentProgress, maxProgress }) => {
 *     console.log(achievementName, currentProgress, '/', maxProgress);
 *   });
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
                    prom.resolve(msg.result);
                }
                return;
            }

            // Steam コールバックイベント
            if (msg.event) {
                const handlers = _eventHandlers.get(msg.event);
                if (handlers) {
                    handlers.forEach((h) => {
                        try { h(msg.params); } catch (err) {
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
     * className と methodName を C# の汎用ディスパッチャへ転送する。
     * @param {string} className  Steamworks クラス名（例: "SteamUserStats"）
     * @param {string} methodName メソッド名（例: "SetAchievement"）
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
