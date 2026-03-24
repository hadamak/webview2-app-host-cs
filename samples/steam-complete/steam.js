/**
 * steam.js
 * WebView2AppHost の Steam ブリッジ JS 側 API。
 *
 * 使い方:
 *   <script src="steam.js"></script>
 *
 *   // 初期化（ユーザー情報・AppID 等を取得）
 *   const info = await Steam.init();
 *   if (info.isAvailable) {
 *     console.log('プレイヤー:', info.personaName);
 *   }
 *
 *   // 実績解除
 *   await Steam.unlockAchievement('FIRST_CLEAR');
 *
 *   // Steam 側 UI を開く
 *   Steam.showOverlay('achievements');
 *
 *   // イベント
 *   Steam.on('on-game-overlay-activated', ({ isShowing }) => {
 *     console.log('steam ui state changed:', isShowing);
 *   });
 *
 * ブラウザで直接開いた場合: isAvailable() が false を返すだけで、
 * エラーにならない（開発中はブラウザで動かし続けられる）。
 *
 * 注意:
 * WebView2 上に Steam オーバーレイが重なるわけではない。
 */
const Steam = (() => {
    const _isHost = typeof window.chrome !== 'undefined' &&
                    typeof window.chrome.webview !== 'undefined';

    let _asyncId = 0;
    const _pending = new Map();

    if (_isHost) {
        window.chrome.webview.addEventListener('message', e => {
            let msg;
            try { msg = JSON.parse(e.data); } catch { return; }
            if (msg.source !== 'steam') return;

            const params = msg.params ?? {};

            if (typeof msg.asyncId === 'number' && msg.asyncId >= 0) {
                const resolve = _pending.get(msg.asyncId);
                if (resolve) {
                    _pending.delete(msg.asyncId);
                    resolve(params);
                    return;
                }
            }

            window.dispatchEvent(
                new CustomEvent(`steam:${msg.messageId}`, { detail: params })
            );
        });
    }

    function postSteamMessage(messageId, params, asyncId) {
        window.chrome.webview.postMessage(JSON.stringify({
            source: 'steam',
            messageId,
            params,
            asyncId
        }));
    }

    function callAsync(messageId, params = []) {
        if (!_isHost) return Promise.resolve({ isAvailable: false });
        return new Promise(resolve => {
            const id = ++_asyncId;
            _pending.set(id, resolve);
            postSteamMessage(messageId, params, id);
        });
    }

    function callSync(messageId, params = []) {
        if (!_isHost) return;
        postSteamMessage(messageId, params, -1);
    }

    const OVERLAY_OPTIONS = [
        'friends', 'community', 'players', 'settings',
        'official-game-group', 'stats', 'achievements'
    ];

    return {
        isAvailable: () => _isHost,
        init: () => callAsync('init'),
        unlockAchievement: (name) => callAsync('set-achievement', [name]),
        clearAchievement: (name) => callAsync('clear-achievement', [name]),
        showOverlay: (option = 'achievements') => {
            const index = OVERLAY_OPTIONS.indexOf(option);
            if (index === -1) {
                console.warn(`[Steam] 不明な UI オプション: ${option}`);
                return;
            }
            callSync('show-overlay', [index]);
        },
        showOverlayURL: (url, modal = false) =>
            callSync('show-overlay-url', [url, modal]),
        showOverlayInviteDialog: (lobbyId) =>
            callSync('show-overlay-invite-dialog', [lobbyId]),
        checkDlcInstalled: (appIds) =>
            callAsync('is-dlc-installed', [appIds.map(String).join(',')]),
        installDlc: (appId) => callSync('install-dlc', [appId]),
        uninstallDlc: (appId) => callSync('uninstall-dlc', [appId]),
        setRichPresence: (key, value) => callSync('set-rich-presence', [key, value]),
        clearRichPresence: () => callSync('clear-rich-presence', []),
        triggerScreenshot: () => callSync('trigger-screenshot', []),
        getAuthTicketForWebApi: (identity = '') =>
            callAsync('get-auth-ticket-for-web-api', [identity]),
        cancelAuthTicket: (authTicket) =>
            callSync('cancel-auth-ticket', [authTicket]),
        on: (event, handler) =>
            window.addEventListener(`steam:${event}`, e => handler(e.detail)),
    };
})();
