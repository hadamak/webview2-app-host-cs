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
 *   // オーバーレイ
 *   Steam.showOverlay('achievements');
 *
 *   // イベント
 *   Steam.on('on-game-overlay-activated', ({ isShowing }) => {
 *     if (isShowing) pause(); else resume();
 *   });
 *
 * ブラウザで直接開いた場合: isAvailable() が false を返すだけで、
 * エラーにならない（開発中はブラウザで動かし続けられる）。
 */
const Steam = (() => {
    // ホスト上で動作しているかどうか
    const _isHost = typeof window.chrome !== 'undefined' &&
                    typeof window.chrome.webview !== 'undefined';

    let _asyncId = 0;
    const _pending = new Map();  // asyncId → resolve 関数

    // ----------------------------------------------------------------
    // C# からのメッセージを受信
    // params は生の JSON 値として届く（二重エンコードなし）
    // ----------------------------------------------------------------
    if (_isHost) {
        window.chrome.webview.addEventListener('message', e => {
            let msg;
            try { msg = JSON.parse(e.data); } catch { return; }
            if (msg.source !== 'steam') return;

            // params は生のオブジェクト/配列として届く
            const params = msg.params ?? {};

            // 非同期レスポンス（asyncId >= 0）
            if (typeof msg.asyncId === 'number' && msg.asyncId >= 0) {
                const resolve = _pending.get(msg.asyncId);
                if (resolve) {
                    _pending.delete(msg.asyncId);
                    resolve(params);
                    return;
                }
            }

            // イベント通知（オーバーレイ・DLC インストール等）
            window.dispatchEvent(
                new CustomEvent(`steam:${msg.messageId}`, { detail: params })
            );
        });
    }

    // ----------------------------------------------------------------
    // 非同期呼び出し（結果を await で受け取る）
    // メッセージ全体を JSON 文字列として送る。
    // params 自体は JSON 配列として埋め込まれ、C# 側がそのまま抽出する。
    // ----------------------------------------------------------------
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

    // ----------------------------------------------------------------
    // 同期呼び出し（結果を待たない）
    // ----------------------------------------------------------------
    function callSync(messageId, params = []) {
        if (!_isHost) return;
        postSteamMessage(messageId, params, -1);
    }

    // ----------------------------------------------------------------
    // オーバーレイオプション（Construct の定義に準拠）
    // ----------------------------------------------------------------
    const OVERLAY_OPTIONS = [
        'friends', 'community', 'players', 'settings',
        'official-game-group', 'stats', 'achievements'
    ];

    // ----------------------------------------------------------------
    // 公開 API
    // ----------------------------------------------------------------
    return {
        /**
         * Steam が使えるか（ホスト外 / steam_bridge.dll なし の場合は false）。
         * init() の結果の isAvailable とは異なり、DLL のロード前でも確認できる。
         */
        isAvailable: () => _isHost,

        /**
         * Steam を初期化してユーザー情報を返す。
         * @returns {{ isAvailable, personaName, accountId, steamId64Bit,
         *             appId, isRunningOnSteamDeck, steamUILanguage,
         *             currentGameLanguage, availableGameLanguages, ... }}
         */
        init: () => callAsync('init'),

        // ---- 実績 ----

        /**
         * 実績を解除する。
         * @param {string} name  Steamworks で設定した実績 API 名
         * @returns {Promise<{ isOk: boolean }>}
         */
        unlockAchievement: (name) =>
            callAsync('set-achievement', [name]),

        /**
         * 実績をリセットする（主にデバッグ用）。
         * @param {string} name
         * @returns {Promise<{ isOk: boolean }>}
         */
        clearAchievement: (name) =>
            callAsync('clear-achievement', [name]),

        // ---- オーバーレイ ----

        /**
         * Steam オーバーレイを開く。
         * @param {'friends'|'community'|'players'|'settings'|
         *         'official-game-group'|'stats'|'achievements'} option
         */
        showOverlay: (option = 'achievements') => {
            const index = OVERLAY_OPTIONS.indexOf(option);
            if (index === -1) {
                console.warn(`[Steam] 不明なオーバーレイオプション: ${option}`);
                return;
            }
            callSync('show-overlay', [index]);
        },

        /**
         * Steam オーバーレイで URL を開く。
         * @param {string} url
         * @param {boolean} modal
         */
        showOverlayURL: (url, modal = false) =>
            callSync('show-overlay-url', [url, modal]),

        /**
         * マルチプレイヤーロビーの招待ダイアログを開く。
         * @param {string} lobbyId  64bit Steam ロビー ID（文字列）
         */
        showOverlayInviteDialog: (lobbyId) =>
            callSync('show-overlay-invite-dialog', [lobbyId]),

        // ---- DLC ----

        /**
         * DLC のインストール状態を確認する。
         * @param {number[]} appIds  DLC の AppID 配列
         * @returns {Promise<{ isOk: boolean, results: string }>}
         */
        checkDlcInstalled: (appIds) =>
            callAsync('is-dlc-installed', [appIds.map(String).join(',')]),

        /** @param {number} appId */
        installDlc:   (appId) => callSync('install-dlc',   [appId]),

        /** @param {number} appId */
        uninstallDlc: (appId) => callSync('uninstall-dlc', [appId]),

        // ---- リッチプレゼンス ----

        /**
         * @param {string} key
         * @param {string} value  空文字でキーを削除
         */
        setRichPresence:   (key, value) => callSync('set-rich-presence',  [key, value]),
        clearRichPresence: ()            => callSync('clear-rich-presence', []),

        // ---- スクリーンショット ----

        /** Steam スクリーンショットをトリガーする */
        triggerScreenshot: () => callSync('trigger-screenshot', []),

        // ---- 認証 ----

        /**
         * Web API 認証チケットを取得する。
         * @param {string} identity  空文字可
         * @returns {Promise<{ isOk: boolean, authTicket: number, ticketHexStr: string }>}
         */
        getAuthTicketForWebApi: (identity = '') =>
            callAsync('get-auth-ticket-for-web-api', [identity]),

        /**
         * 認証チケットをキャンセルする。
         * @param {number} authTicket  getAuthTicketForWebApi で取得した値
         */
        cancelAuthTicket: (authTicket) =>
            callSync('cancel-auth-ticket', [authTicket]),

        // ---- イベントリスナー ----

        /**
         * Steam イベントを購読する。
         *
         * イベント名一覧:
         *   'on-game-overlay-activated'  { isShowing: boolean }
         *   'on-dlc-installed'           { appId: number }
         *   'screenshot-requested'       {}
         *
         * @param {string} event
         * @param {function} handler
         */
        on: (event, handler) =>
            window.addEventListener(`steam:${event}`,
                e => handler(e.detail)),
    };
})();
