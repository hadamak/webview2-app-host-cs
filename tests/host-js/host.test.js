// host.test.js
// Unit tests for the 3-tier Proxy based host.js (Universal Plugin Architecture).
// Run with: node tests/host-js/host.test.js
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

const fs      = require('fs');
const path    = require('path');
const hostSrc = fs.readFileSync(path.join(__dirname, '../../web-content/host.js'), 'utf8');

/**
 * host.js を独立した window モック環境でロードして返す。
 */
function loadHost() {
    const messages       = [];
    let   bridgeCallback = null;
    const windowMock = {
        chrome: {
            webview: {
                postMessage(m) { messages.push(JSON.parse(m)); },
                addEventListener(_ev, handler) { bridgeCallback = handler; },
            },
        },
    };
    // Host と Steam の両方を取り出す
    const exports = new Function('window', `
        ${hostSrc};
        return { Host, Steam };
    `)(windowMock);
    return {
        Host: exports.Host,
        Steam: exports.Steam,
        messages,
        getBridgeCallback: () => bridgeCallback,
    };
}

/** C# からのメッセージをシミュレートする */
function simulate(getBridgeCallback, data) {
    getBridgeCallback()({ data: JSON.stringify(data) });
}

async function main() {
    console.log('=== host.js UPA Proxy Tests ===');

    // -------------------------------------------------------------------------
    // 1. 3階層プロキシ — invoke メッセージのフォーマット
    // -------------------------------------------------------------------------
    {
        const { Host, messages } = loadHost();
        Host.Steam.SteamUserStats.SetStat('NumGames', 5);
        const msg = messages.pop();
        expect(msg.source              === 'Steam',           '1. source がプラグイン名 "Steam"');
        expect(msg.messageId           === 'invoke',          '1. messageId が invoke');
        expect(msg.params.className    === 'SteamUserStats',  '1. className が正しい');
        expect(msg.params.methodName   === 'SetStat',         '1. methodName が正しい');
        expect(msg.params.args[0]      === 'NumGames',        '1. 第1引数が正しい');
        expect(msg.params.args[1]      === 5,                 '1. 第2引数が正しい');
    }

    // -------------------------------------------------------------------------
    // 2. 別プラグイン — Node.ClassName.method でも同じ構造が生成される
    // -------------------------------------------------------------------------
    {
        const { Host, messages } = loadHost();
        Host.Node.ImageProcessor.resize('photo.jpg', 300);
        const msg = messages.pop();
        expect(msg.source              === 'Node',            '2. source が "Node"');
        expect(msg.params.className    === 'ImageProcessor',  '2. className が正しい');
        expect(msg.params.methodName   === 'resize',          '2. methodName が正しい');
        expect(msg.params.args[0]      === 'photo.jpg',       '2. 第1引数が正しい');
        expect(msg.params.args[1]      === 300,               '2. 第2引数が正しい');
    }

    // -------------------------------------------------------------------------
    // 3. .then/.catch/.finally はすべての階層で undefined
    // -------------------------------------------------------------------------
    {
        const { Host } = loadHost();
        // クラス階層
        expect(Host.Steam.SteamClient.then    === undefined, '3. クラス層 .then は undefined');
        expect(Host.Steam.SteamClient.catch   === undefined, '3. クラス層 .catch は undefined');
        expect(Host.Steam.SteamClient.finally === undefined, '3. クラス層 .finally は undefined');
        // プラグイン階層
        expect(Host.Steam.then    === undefined, '3. プラグイン層 .then は undefined');
        expect(Host.Steam.catch   === undefined, '3. プラグイン層 .catch は undefined');
        expect(Host.Steam.finally === undefined, '3. プラグイン層 .finally は undefined');
    }

    // -------------------------------------------------------------------------
    // 4. asyncId の単調増加・プラグインをまたいでも重複しない
    // -------------------------------------------------------------------------
    {
        const { Host, messages } = loadHost();
        Host.Steam.SteamUserStats.GetStatInt('NumWins');
        Host.Node.Node.version();
        Host.Steam.SteamFriends.GetPersonaName();
        const ids = messages.map(m => m.asyncId);
        expect(ids[0] < ids[1] && ids[1] < ids[2], '4. asyncId が単調増加する');
        expect(new Set(ids).size === 3,             '4. asyncId に重複がない（プラグイン跨ぎ）');
    }

    // -------------------------------------------------------------------------
    // 5. invoke-result → Promise が正しい pluginName で resolve される
    // -------------------------------------------------------------------------
    {
        const { Host, messages, getBridgeCallback } = loadHost();
        const p = Host.Steam.SteamUserStats.GetStatInt('NumWins');
        const msg = messages.pop();
        simulate(getBridgeCallback, {
            source: 'Steam', messageId: 'invoke-result',
            result: 99, asyncId: msg.asyncId,
        });
        const result = await p;
        expect(result === 99, '5. invoke-result の result 値で resolve される');
    }

    // -------------------------------------------------------------------------
    // 6. invoke-result error → reject のメッセージにプラグイン名が入る
    // -------------------------------------------------------------------------
    {
        const { Host, messages, getBridgeCallback } = loadHost();
        const p = Host.Node.Node.version();
        const msg = messages.pop();
        simulate(getBridgeCallback, {
            source: 'Node', messageId: 'invoke-result',
            error: 'node not found', asyncId: msg.asyncId,
        });
        let caught = null;
        await p.catch(e => { caught = e; });
        expect(caught instanceof Error,                '6. error 応答は Error として reject される');
        expect(caught.message.includes('Node'),        '6. エラーメッセージにプラグイン名が含まれる');
        expect(caught.message.includes('node not found'), '6. エラー本文が含まれる');
    }

    // -------------------------------------------------------------------------
    // 7. イベントの受信（Host.on/off）
    // -------------------------------------------------------------------------
    {
        const { Host, getBridgeCallback } = loadHost();
        let received = null;
        Host.on('OnAchievementProgress', (p) => { received = p; });
        simulate(getBridgeCallback, {
            source: 'Steam', event: 'OnAchievementProgress',
            params: { achievementName: 'ACH_WIN', currentProgress: 3, maxProgress: 10 },
        });
        expect(received !== null,                       '7. イベントハンドラが呼ばれる');
        expect(received.achievementName === 'ACH_WIN',  '7. params が正しく渡される');
    }

    // -------------------------------------------------------------------------
    // 8. Host.off によるハンドラ解除
    // -------------------------------------------------------------------------
    {
        const { Host, getBridgeCallback } = loadHost();
        let count = 0;
        const h = () => count++;
        Host.on('TestEvent', h);
        simulate(getBridgeCallback, { source: 'Steam', event: 'TestEvent', params: {} });
        Host.off('TestEvent', h);
        simulate(getBridgeCallback, { source: 'Steam', event: 'TestEvent', params: {} });
        expect(count === 1, '8. off 後はハンドラが呼ばれない');
    }

    // -------------------------------------------------------------------------
    // 9. ハンドル付き invoke-result → インスタンスプロキシ（プラグイン名保持）
    // -------------------------------------------------------------------------
    {
        const { Host, messages, getBridgeCallback } = loadHost();
        const p = Host.Steam.SteamUserStats.FindOrCreateLeaderboardAsync('Board', 2, 1);
        const inv = messages.pop();
        simulate(getBridgeCallback, {
            source: 'Steam', messageId: 'invoke-result',
            result: { __isHandle: true, __handleId: 42, className: 'Leaderboard' },
            asyncId: inv.asyncId,
        });
        const board = await p;
        expect(board !== null,           '9. ハンドルはオブジェクトとして返る');

        board.SubmitScoreAsync(5000);
        const instMsg = messages.pop();
        expect(instMsg.source              === 'Steam',            '9. インスタンス呼び出しの source は Steam');
        expect(instMsg.params.handleId     === 42,                 '9. handleId が正しく転送される');
        expect(instMsg.params.methodName   === 'SubmitScoreAsync', '9. methodName が正しい');
    }

    // -------------------------------------------------------------------------
    // 10. インスタンスプロキシの .then/.catch/.finally は undefined
    // -------------------------------------------------------------------------
    {
        const { Host, messages, getBridgeCallback } = loadHost();
        const p = Host.Steam.SteamUserStats.FindOrCreateLeaderboardAsync('Board', 2, 1);
        const inv = messages.pop();
        simulate(getBridgeCallback, {
            source: 'Steam', messageId: 'invoke-result',
            result: { __isHandle: true, __handleId: 99, className: 'Leaderboard' },
            asyncId: inv.asyncId,
        });
        const board = await p;
        expect(board.then    === undefined, '10. インスタンスプロキシ .then は undefined');
        expect(board.catch   === undefined, '10. インスタンスプロキシ .catch は undefined');
        expect(board.finally === undefined, '10. インスタンスプロキシ .finally は undefined');
    }

    // -------------------------------------------------------------------------
    // 11. 後方互換 Steam グローバル — steam.js 相当のメッセージフォーマット
    //     Steam.ClassName.method → source:"Steam" で送信される
    // -------------------------------------------------------------------------
    {
        const { Steam, messages } = loadHost();
        Steam.SteamUserStats.SetStat('NumGames', 1);
        const msg = messages.pop();
        expect(msg.source              === 'Steam',          '11. 後方互換 Steam: source が "Steam"');
        expect(msg.params.className    === 'SteamUserStats', '11. 後方互換 Steam: className が正しい');
        expect(msg.params.methodName   === 'SetStat',        '11. 後方互換 Steam: methodName が正しい');
    }

    // -------------------------------------------------------------------------
    // 12. 後方互換 Steam.on → Host と同じイベントシステムを共有する
    // -------------------------------------------------------------------------
    {
        const { Host, Steam, getBridgeCallback } = loadHost();
        let hostCount  = 0;
        let steamCount = 0;
        Host.on('SharedEvent',  () => hostCount++);
        Steam.on('SharedEvent', () => steamCount++);
        simulate(getBridgeCallback, { source: 'Steam', event: 'SharedEvent', params: {} });
        expect(hostCount  === 1, '12. Host.on ハンドラが呼ばれる');
        expect(steamCount === 1, '12. Steam.on ハンドラも呼ばれる（同一イベントシステム）');
    }

    // -------------------------------------------------------------------------
    // 13. ホスト外（非 WebView2）では Mock モードで即 resolve
    // -------------------------------------------------------------------------
    {
        const HostMock = new Function(`${hostSrc}; return Host;`)();
        let resolved = false;
        await HostMock.Steam.SteamUserStats.GetStatInt('NumWins').then(() => { resolved = true; });
        expect(resolved, '13. 非ホスト環境で Promise が resolve される');
    }

    // -------------------------------------------------------------------------
    // 14. source フィールドのないメッセージ（visibilityChange 等）は無視される
    // -------------------------------------------------------------------------
    {
        const { Host, getBridgeCallback } = loadHost();
        let called = false;
        Host.on('AnyEvent', () => { called = true; });
        // source なしのシステムメッセージ
        getBridgeCallback()({ data: JSON.stringify({ event: 'visibilityChange', state: 'hidden' }) });
        expect(!called, '14. source なしメッセージはイベントハンドラに届かない');
    }

    // -------------------------------------------------------------------------
    // 15. 配列を含む invoke-result — 要素がハンドルならプロキシ化される
    // -------------------------------------------------------------------------
    {
        const { Host, messages, getBridgeCallback } = loadHost();
        const p = Host.Node.FileSystem.listFiles('/tmp');
        const inv = messages.pop();
        simulate(getBridgeCallback, {
            source: 'Node', messageId: 'invoke-result',
            result: [
                { __isHandle: true, __handleId: 1, className: 'FileEntry' },
                { __isHandle: true, __handleId: 2, className: 'FileEntry' },
            ],
            asyncId: inv.asyncId,
        });
        const files = await p;
        expect(Array.isArray(files),    '15. 配列結果は配列として返る');
        expect(files.length === 2,      '15. 配列要素数が正しい');

        files[0].getName();
        const m = messages.pop();
        expect(m.source            === 'Node',     '15. 配列ハンドルの source は Node');
        expect(m.params.handleId   === 1,          '15. 配列[0] の handleId が正しい');
        expect(m.params.methodName === 'getName',  '15. 配列[0] のメソッドが正しい');
    }

    // -------------------------------------------------------------------------
    const total = pass + fail;
    console.log(`\n${pass} / ${total} passed${fail === 0 ? '  -- ALL PASSED' : `  -- ${fail} FAILED`}`);
    process.exit(fail === 0 ? 0 : 1);
}

main().catch(console.error);
