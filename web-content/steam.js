/**
 * steam.js
 * WebView2AppHost の Steam ブリッジ JS 側 API。
 *
 * 注意:
 * WebView2 の画面上に Steam オーバーレイが重なるわけではない。
 * showOverlay 系 API は Steam 側の関連 UI を開く用途として扱う。
 */
const Steam = (() => {
    const _isHost = typeof window.chrome !== 'undefined' &&
                    typeof window.chrome.webview !== 'undefined';

    let _asyncId = 0;
    const _pending = new Map();

    const OVERLAY_OPTIONS = [
        'friends', 'community', 'players', 'settings',
        'official-game-group', 'stats', 'achievements'
    ];

    const LEADERBOARD_SORT_METHODS = {
        ascending: 1,
        descending: 2,
    };

    const LEADERBOARD_DISPLAY_TYPES = {
        numeric: 1,
        'time-seconds': 2,
        'time-milliseconds': 3,
    };

    const LEADERBOARD_UPLOAD_METHODS = {
        'keep-best': 1,
        'force-update': 2,
    };

    const LEADERBOARD_DATA_REQUESTS = {
        global: 0,
        'global-around-user': 1,
        friends: 2,
        users: 3,
    };

    function parseJsonField(value, fallback) {
        if (typeof value !== 'string' || value === '') return fallback;
        try {
            return JSON.parse(value);
        } catch {
            return fallback;
        }
    }

    function resolveEnum(map, value, kind) {
        if (typeof value === 'number') return value;
        const resolved = map[value];
        if (typeof resolved === 'number') return resolved;
        throw new Error(`[Steam] Unknown ${kind}: ${value}`);
    }

    function toCsv(values) {
        return (values || []).map(v => String(v)).join(',');
    }

    function encodeBase64FromBytes(bytes) {
        if (typeof Buffer !== 'undefined') {
            return Buffer.from(bytes).toString('base64');
        }

        let binary = '';
        for (const byte of bytes) binary += String.fromCharCode(byte);
        return btoa(binary);
    }

    function decodeBase64ToBytes(base64) {
        if (typeof Buffer !== 'undefined') {
            return Uint8Array.from(Buffer.from(base64, 'base64'));
        }

        const binary = atob(base64);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        return bytes;
    }

    function encodeTextToBase64(text) {
        if (typeof TextEncoder !== 'undefined') {
            return encodeBase64FromBytes(new TextEncoder().encode(text));
        }

        if (typeof Buffer !== 'undefined') {
            return Buffer.from(text, 'utf8').toString('base64');
        }

        throw new Error('[Steam] TextEncoder is not available');
    }

    function decodeBase64ToText(base64) {
        const bytes = decodeBase64ToBytes(base64);

        if (typeof TextDecoder !== 'undefined') {
            return new TextDecoder().decode(bytes);
        }

        if (typeof Buffer !== 'undefined') {
            return Buffer.from(bytes).toString('utf8');
        }

        throw new Error('[Steam] TextDecoder is not available');
    }

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
        if (!_isHost) return Promise.resolve({ isAvailable: false, isOk: false });
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

    return {
        constants: {
            leaderboardSortMethods: { ...LEADERBOARD_SORT_METHODS },
            leaderboardDisplayTypes: { ...LEADERBOARD_DISPLAY_TYPES },
            leaderboardUploadMethods: { ...LEADERBOARD_UPLOAD_METHODS },
            leaderboardDataRequests: { ...LEADERBOARD_DATA_REQUESTS },
        },

        isAvailable: () => _isHost,

        init: () => callAsync('init'),

        unlockAchievement: (name) =>
            callAsync('set-achievement', [name]),

        clearAchievement: (name) =>
            callAsync('clear-achievement', [name]),

        getAchievementState: (name) =>
            callAsync('get-achievement-state', [name]),

        getStatInt: (name) =>
            callAsync('get-stat-int', [name]),

        getStatFloat: (name) =>
            callAsync('get-stat-float', [name]),

        setStatInt: (name, value) =>
            callAsync('set-stat-int', [name, value]),

        setStatFloat: (name, value) =>
            callAsync('set-stat-float', [name, value]),

        storeStats: () =>
            callAsync('store-stats', []),

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

        installDlc: (appId) =>
            callSync('install-dlc', [appId]),

        uninstallDlc: (appId) =>
            callSync('uninstall-dlc', [appId]),

        getDlcList: async () => {
            const result = await callAsync('get-dlc-list', []);
            result.dlc = parseJsonField(result.dlcJson, []);
            return result;
        },

        getAppOwnershipInfo: () =>
            callAsync('get-app-ownership-info', []),

        isSubscribedApp: (appId) =>
            callAsync('is-subscribed-app', [appId]),

        getCloudStatus: () =>
            callAsync('cloud-get-status', []),

        listCloudFiles: async () => {
            const result = await callAsync('cloud-list-files', []);
            result.files = parseJsonField(result.filesJson, []);
            return result;
        },

        cloudFileExists: (fileName) =>
            callAsync('cloud-file-exists', [fileName]),

        readCloudFile: (fileName) =>
            callAsync('cloud-read-file', [fileName]),

        readCloudFileText: async (fileName) => {
            const result = await callAsync('cloud-read-file', [fileName]);
            if (result.isOk && typeof result.dataBase64 === 'string')
                result.text = decodeBase64ToText(result.dataBase64);
            return result;
        },

        writeCloudFile: (fileName, dataBase64) =>
            callAsync('cloud-write-file', [fileName, dataBase64]),

        writeCloudFileText: (fileName, text) =>
            callAsync('cloud-write-file', [fileName, encodeTextToBase64(text)]),

        deleteCloudFile: (fileName) =>
            callAsync('cloud-delete-file', [fileName]),

        findLeaderboard: (name) =>
            callAsync('find-leaderboard', [name]),

        findOrCreateLeaderboard: (name, sortMethod = 'descending', displayType = 'numeric') =>
            callAsync('find-or-create-leaderboard', [
                name,
                resolveEnum(LEADERBOARD_SORT_METHODS, sortMethod, 'leaderboard sort method'),
                resolveEnum(LEADERBOARD_DISPLAY_TYPES, displayType, 'leaderboard display type')
            ]),

        uploadLeaderboardScore: (leaderboardHandle, score, options = {}) =>
            callAsync('upload-leaderboard-score', [
                String(leaderboardHandle),
                resolveEnum(
                    LEADERBOARD_UPLOAD_METHODS,
                    options.uploadMethod ?? 'keep-best',
                    'leaderboard upload method'),
                score,
                toCsv(options.details ?? [])
            ]),

        downloadLeaderboardEntries: async (
            leaderboardHandle,
            requestType = 'global',
            rangeStart = 0,
            rangeEnd = 9
        ) => {
            const result = await callAsync('download-leaderboard-entries', [
                String(leaderboardHandle),
                resolveEnum(LEADERBOARD_DATA_REQUESTS, requestType, 'leaderboard data request'),
                rangeStart,
                rangeEnd
            ]);
            result.entries = parseJsonField(result.entriesJson, []);
            return result;
        },

        setRichPresence: (key, value) =>
            callSync('set-rich-presence', [key, value]),

        clearRichPresence: () =>
            callSync('clear-rich-presence', []),

        triggerScreenshot: () =>
            callSync('trigger-screenshot', []),

        getAuthTicketForWebApi: (identity = '') =>
            callAsync('get-auth-ticket-for-web-api', [identity]),

        cancelAuthTicket: (authTicket) =>
            callSync('cancel-auth-ticket', [authTicket]),

        decodeBase64ToText,
        encodeTextToBase64,

        on: (event, handler) =>
            window.addEventListener(`steam:${event}`,
                e => handler(e.detail)),
    };
})();
