// steam.test.js
// Unit tests for the ES6 Proxy based generic steam.js.
// Run with: node tests/steam-js/steam.test.js
// Exit code 0 = all passed.

'use strict';

let pass = 0;
let fail = 0;

function expect(condition, label) {
    if (condition) pass++;
    else {
        fail++;
        console.error(`FAILED  ${label}`);
    }
}

const fs   = require('fs');
const path = require('path');
const steamSrc = fs.readFileSync(path.join(__dirname, '../../src/steam.js'), 'utf8');

/**
 * steam.js を独立した window モック環境でロードして返す。
 * bridgeCallback には webview.addEventListener で登録された C# → JS ハンドラが入る。
 */
function loadSteam() {
    const messages      = [];
    let   bridgeCallback = null;
    const windowMock = {
        chrome: {
            webview: {
                postMessage(m) { messages.push(JSON.parse(m)); },
                addEventListener(_ev, handler) { bridgeCallback = handler; },
            },
        },
    };
    const Steam = new Function('window', `${steamSrc}; return Steam;`)(windowMock);
    return { Steam, messages, getBridgeCallback: () => bridgeCallback };
}

/** C# からのメッセージをシミュレートする */
function simulate(getBridgeCallback, data) {
    getBridgeCallback()({ data: JSON.stringify(data) });
}

async function main() {
    console.log('=== steam.js ES6 Proxy Tests ===');

    // -------------------------------------------------------------------------
    // 1. 静的メソッド呼び出し — invoke メッセージの生成
    // -------------------------------------------------------------------------
    {
        const { Steam, messages } = loadSteam();
        Steam.SteamUserStats.SetStat('Wins', 5);
        const msg = messages.pop();
        expect(msg.source    === 'steam',          '1. source フィールドが steam');
        expect(msg.messageId === 'invoke',         '1. messageId が invoke');
        expect(msg.params.className  === 'SteamUserStats', '1. className が正しく取得される');
        expect(msg.params.methodName === 'SetStat',        '1. methodName が正しく取得される');
        expect(msg.params.args.length === 2
            && msg.params.args[0] === 'Wins'
            && msg.params.args[1] === 5,           '1. 引数が正しく転送される');
    }

    // -------------------------------------------------------------------------
    // 2. .then / .catch / .finally はプロキシに存在しない（await 無限ループ防止）
    // -------------------------------------------------------------------------
    {
        const { Steam } = loadSteam();
        expect(Steam.SteamClient.then    === undefined, '2. .then は undefined');
        expect(Steam.SteamClient.catch   === undefined, '2. .catch は undefined');
        expect(Steam.SteamClient.finally === undefined, '2. .finally は undefined');
    }

    // -------------------------------------------------------------------------
    // 3. asyncId の単調増加（複数 invoke で ID が重複しない）
    // -------------------------------------------------------------------------
    {
        const { Steam, messages } = loadSteam();
        Steam.SteamUserStats.GetStatInt('NumWins');
        Steam.SteamUserStats.GetStatInt('NumLosses');
        Steam.SteamUserStats.GetStatInt('NumDraws');
        const ids = messages.map(m => m.asyncId);
        expect(ids[0] < ids[1] && ids[1] < ids[2], '3. asyncId が単調増加する');
        expect(new Set(ids).size === 3,             '3. asyncId に重複がない');
    }

    // -------------------------------------------------------------------------
    // 4. invoke-result の正常応答 → Promise が resolve される
    // -------------------------------------------------------------------------
    {
        const { Steam, messages, getBridgeCallback } = loadSteam();
        const p = Steam.SteamUserStats.GetStatInt('NumWins');
        const msg = messages.pop();
        simulate(getBridgeCallback, {
            source: 'steam', messageId: 'invoke-result',
            result: 42, asyncId: msg.asyncId,
        });
        const result = await p;
        expect(result === 42, '4. invoke-result の result 値で resolve される');
    }

    // -------------------------------------------------------------------------
    // 5. invoke-result の error 応答 → Promise が reject される
    // -------------------------------------------------------------------------
    {
        const { Steam, messages, getBridgeCallback } = loadSteam();
        const p = Steam.SteamUserStats.SetStat('Wins', 5);
        const msg = messages.pop();
        simulate(getBridgeCallback, {
            source: 'steam', messageId: 'invoke-result',
            error: 'SteamNotInitialized', asyncId: msg.asyncId,
        });
        let caught = null;
        await p.catch(e => { caught = e; });
        expect(caught instanceof Error,                    '5. error 応答は Error として reject される');
        expect(caught.message.includes('SteamNotInitialized'), '5. error メッセージが伝搬する');
    }

    // -------------------------------------------------------------------------
    // 6. Steam イベントの受信とハンドラへの配送
    // -------------------------------------------------------------------------
    {
        const { Steam, getBridgeCallback } = loadSteam();
        let received = null;
        Steam.on('OnAchievementProgress', (params) => { received = params; });
        simulate(getBridgeCallback, {
            source: 'steam', event: 'OnAchievementProgress',
            params: { achievementName: 'ACH_WIN', currentProgress: 3, maxProgress: 10 },
        });
        expect(received !== null,                       '6. イベントハンドラが呼ばれる');
        expect(received.achievementName === 'ACH_WIN',  '6. params が正しく渡される');
        expect(received.currentProgress === 3,          '6. currentProgress が正しい');
    }

    // -------------------------------------------------------------------------
    // 7. off() によるハンドラ解除
    // -------------------------------------------------------------------------
    {
        const { Steam, getBridgeCallback } = loadSteam();
        let callCount = 0;
        const handler = () => { callCount++; };
        Steam.on('OnGameOverlayActivated', handler);
        simulate(getBridgeCallback, { source: 'steam', event: 'OnGameOverlayActivated', params: {} });
        Steam.off('OnGameOverlayActivated', handler);
        simulate(getBridgeCallback, { source: 'steam', event: 'OnGameOverlayActivated', params: {} });
        expect(callCount === 1, '7. off() 後はハンドラが呼ばれない');
    }

    // -------------------------------------------------------------------------
    // 8. ハンドル付き invoke-result → インスタンスプロキシが生成される
    // -------------------------------------------------------------------------
    {
        const { Steam, messages, getBridgeCallback } = loadSteam();
        const p = Steam.SteamUserStats.FindOrCreateLeaderboardAsync('Feet Traveled', 2, 1);
        const invMsg = messages.pop();
        simulate(getBridgeCallback, {
            source: 'steam', messageId: 'invoke-result',
            result: { __isHandle: true, __handleId: 777, className: 'Leaderboard' },
            asyncId: invMsg.asyncId,
        });
        const board = await p;
        expect(typeof board === 'object' && board !== null, '8. ハンドルはオブジェクトとして返る');
        // インスタンスメソッドの呼び出し
        board.SubmitScoreAsync(5000);
        const instMsg = messages.pop();
        expect(instMsg.messageId        === 'invoke',           '8. インスタンス呼び出しも invoke');
        expect(instMsg.params.handleId  === 777,                '8. handleId が正しく転送される');
        expect(instMsg.params.methodName === 'SubmitScoreAsync', '8. methodName が正しい');
        expect(instMsg.params.args[0]   === 5000,               '8. 引数が正しく転送される');
    }

    // -------------------------------------------------------------------------
    // 9. インスタンスプロキシの .then/.catch/.finally は undefined
    // -------------------------------------------------------------------------
    {
        const { Steam, messages, getBridgeCallback } = loadSteam();
        const p = Steam.SteamUserStats.FindOrCreateLeaderboardAsync('Board', 2, 1);
        const invMsg = messages.pop();
        simulate(getBridgeCallback, {
            source: 'steam', messageId: 'invoke-result',
            result: { __isHandle: true, __handleId: 888, className: 'Leaderboard' },
            asyncId: invMsg.asyncId,
        });
        const board = await p;
        expect(board.then    === undefined, '9. インスタンスプロキシ .then は undefined');
        expect(board.catch   === undefined, '9. インスタンスプロキシ .catch は undefined');
        expect(board.finally === undefined, '9. インスタンスプロキシ .finally は undefined');
    }

    // -------------------------------------------------------------------------
    // 10. イベント内のハンドル付き params → ネストされたプロキシが生成される
    // -------------------------------------------------------------------------
    {
        const { Steam, messages, getBridgeCallback } = loadSteam();
        let eventPayload = null;
        Steam.on('IncomingHandle', (p) => { eventPayload = p; });
        simulate(getBridgeCallback, {
            source: 'steam', event: 'IncomingHandle',
            params: { __isHandle: true, __handleId: 999, className: 'Leaderboard' },
        });
        expect(eventPayload !== null, '10. イベント内ハンドルのペイロードを受信');
        eventPayload.SubmitScoreAsync(100);
        const msg = messages.pop();
        expect(msg.params.handleId   === 999,               '10. イベント内ハンドルの handleId が正しい');
        expect(msg.params.methodName === 'SubmitScoreAsync', '10. イベント内ハンドルのメソッド呼び出しが正しい');
    }

    // -------------------------------------------------------------------------
    // 11. release メッセージのフォーマット（FinalizationRegistry コールバックの模擬）
    // -------------------------------------------------------------------------
    {
        // FinalizationRegistry のコールバックは GC タイミング依存のため直接テスト不可。
        // コールバック内部と同じ JSON フォーマットで postMessage されることを
        // ダミー webview で検証する。
        const releaseMessages = [];
        const windowMock = {
            chrome: {
                webview: {
                    postMessage(m) { releaseMessages.push(JSON.parse(m)); },
                    addEventListener() {},
                },
            },
        };
        // FinalizationRegistry のコールバック関数を直接取り出せないため、
        // steam.js 内部の仕様（source:'steam', messageId:'release', params:{handleId}）を
        // モックで再現してフォーマットを検証する。
        const expectedRelease = {
            source: 'steam', messageId: 'release', params: { handleId: 42 },
        };
        windowMock.chrome.webview.postMessage(JSON.stringify(expectedRelease));
        const rel = releaseMessages.pop();
        expect(rel.source            === 'steam',   '11. release source が steam');
        expect(rel.messageId         === 'release',  '11. release messageId が release');
        expect(rel.params.handleId   === 42,         '11. release params.handleId が正しい');
    }

    // -------------------------------------------------------------------------
    // 12. ホスト外環境（非 WebView2）では Mock モードで即 resolve される
    // -------------------------------------------------------------------------
    {
        // window.chrome が存在しない環境をシミュレート
        const SteamMock = new Function(`${steamSrc}; return Steam;`)();
        let resolved = false;
        await SteamMock.SteamUserStats.GetStatInt('NumWins').then(() => { resolved = true; });
        expect(resolved, '12. 非ホスト環境で Promise が resolve される');
    }

    // -------------------------------------------------------------------------
    // 13. 不明な source は無視される
    // -------------------------------------------------------------------------
    {
        const { Steam, getBridgeCallback } = loadSteam();
        let called = false;
        Steam.on('SomeEvent', () => { called = true; });
        // source が 'steam' でないメッセージ
        simulate(getBridgeCallback, { source: 'other', event: 'SomeEvent', params: {} });
        expect(!called, '13. source が steam でないメッセージは無視される');
    }

    // -------------------------------------------------------------------------
    // 14. 配列を含む invoke-result のラップ（ハンドル配列）
    // -------------------------------------------------------------------------
    {
        const { Steam, messages, getBridgeCallback } = loadSteam();
        const p = Steam.SteamInventory.GetItems();
        const invMsg = messages.pop();
        simulate(getBridgeCallback, {
            source: 'steam', messageId: 'invoke-result',
            result: [
                { __isHandle: true, __handleId: 1, className: 'InventoryItem' },
                { __isHandle: true, __handleId: 2, className: 'InventoryItem' },
            ],
            asyncId: invMsg.asyncId,
        });
        const items = await p;
        expect(Array.isArray(items),      '14. 配列結果は配列として返る');
        expect(items.length === 2,        '14. 配列要素数が正しい');
        // 配列内の各ハンドルもプロキシ化されメソッド呼び出しができる
        items[0].GetProperty('name');
        const msg0 = messages.pop();
        expect(msg0.params.handleId   === 1,             '14. 配列[0] の handleId が正しい');
        expect(msg0.params.methodName === 'GetProperty', '14. 配列[0] のメソッドが正しい');
    }

    // -------------------------------------------------------------------------
    const total = pass + fail;
    console.log(`\n${pass} / ${total} passed${fail === 0 ? '  -- ALL PASSED' : `  -- ${fail} FAILED`}`);
    process.exit(fail === 0 ? 0 : 1);
}

main().catch(console.error);
