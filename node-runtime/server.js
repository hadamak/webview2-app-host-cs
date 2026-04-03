/**
 * server.js — WebView2AppHost Node.js サイドカー
 *
 * C# (GenericSidecarPlugin.cs) の子プロセスとして起動される。
 * stdin から改行区切りの JSON (NDJSON) を受け取り、
 * 登録されたハンドラを呼び出して結果を stdout に書き出す。
 *
 * プロトコル（JS ↔ C# ↔ このプロセス）:
 *   C# → stdin: { "source":"Node", "messageId":"invoke",
 *                  "params":{ "className":"ClassName", "methodName":"method", "args":[...] },
 *                  "asyncId": N }
 *
 *   stdout → C#: { "source":"Node", "messageId":"invoke-result",
 *                   "result": <value>, "asyncId": N }
 *          または: { "source":"Node", "messageId":"invoke-result",
 *                   "error": "<message>", "asyncId": N }
 *
 * ハンドラの登録:
 *   require('./server').register('MyClass', {
 *     async myMethod(arg1, arg2) { return arg1 + arg2; }
 *   });
 *
 * 利用例（host.js 経由で JS 側から呼ぶ場合）:
 *   const result = await Host.Node.MyClass.myMethod('hello', 'world');
 */

'use strict';

// ---------------------------------------------------------------------------
// ハンドラレジストリ
// ---------------------------------------------------------------------------

/** @type {Map<string, Record<string, Function>>} className → メソッドマップ */
const _handlers = new Map();

/**
 * クラス名とメソッドマップを登録する。
 * @param {string} className
 * @param {Record<string, Function>} methods
 */
function register(className, methods) {
    _handlers.set(className, methods);
}

// ---------------------------------------------------------------------------
// 送受信ユーティリティ
// ---------------------------------------------------------------------------

/**
 * stdout に1行の NDJSON を書き出す。
 * @param {object} obj
 */
function send(obj) {
    process.stdout.write(JSON.stringify(obj) + '\n');
}

/**
 * 成功結果を C# へ返す。
 * @param {number} asyncId
 * @param {any}    result
 */
function resolve(asyncId, result) {
    send({ source: 'Node', messageId: 'invoke-result', result, asyncId });
}

/**
 * エラーを C# へ返す。
 * @param {number} asyncId
 * @param {string} message
 */
function reject(asyncId, message) {
    send({ source: 'Node', messageId: 'invoke-result', error: String(message), asyncId });
}

// ---------------------------------------------------------------------------
// メッセージディスパッチ
// ---------------------------------------------------------------------------

/**
 * C# から届いたメッセージを適切なハンドラへ振り分ける。
 * @param {object} msg
 */
async function dispatch(msg) {
    const { asyncId, messageId, params } = msg;

    if (messageId !== 'invoke') {
        // release 等のノーレスポンスメッセージは無視
        return;
    }

    const { className, methodName, args = [] } = params ?? {};

    try {
        const classHandlers = _handlers.get(className);
        if (!classHandlers) {
            reject(asyncId, `NodePlugin: クラス '${className}' が登録されていません`);
            return;
        }

        const fn = classHandlers[methodName];
        if (typeof fn !== 'function') {
            reject(asyncId, `NodePlugin: ${className}.${methodName} が見つかりません`);
            return;
        }

        const result = await fn(...args);
        resolve(asyncId, result ?? null);
    } catch (err) {
        reject(asyncId, err?.message ?? String(err));
    }
}

// ---------------------------------------------------------------------------
// stdin NDJSON リーダー
// ---------------------------------------------------------------------------

{
    let buffer = '';

    process.stdin.setEncoding('utf8');

    process.stdin.on('data', (chunk) => {
        buffer += chunk;
        const lines = buffer.split('\n');
        // 最後の要素は改行前の不完全な行かもしれないので buffer に戻す
        buffer = lines.pop() ?? '';

        for (const line of lines) {
            const trimmed = line.trim();
            if (!trimmed) continue;
            try {
                const msg = JSON.parse(trimmed);
                dispatch(msg).catch((err) => {
                    process.stderr.write(`[server.js] dispatch error: ${err}\n`);
                });
            } catch (parseErr) {
                process.stderr.write(`[server.js] JSON parse error: ${parseErr}\n`);
            }
        }
    });

    process.stdin.on('end', () => {
        // C# プロセスが stdin を閉じた = ホストが終了した
        process.exit(0);
    });
}

// ---------------------------------------------------------------------------
// デフォルトハンドラ（組み込み）
// ---------------------------------------------------------------------------

register('Node', {
    /** バージョン情報を返す。動作確認用。 */
    version() {
        return {
            node: process.version,
            platform: process.platform,
            arch: process.arch,
        };
    },

    /** npm パッケージが require できるかを確認する。 */
    hasModule(name) {
        try {
            require.resolve(name);
            return true;
        } catch {
            return false;
        }
    },

    /** require してモジュールのバージョンを返す（package.json が必要）。 */
    moduleVersion(name) {
        try {
            const pkg = require(`${name}/package.json`);
            return pkg.version ?? null;
        } catch {
            return null;
        }
    },
});

// ---------------------------------------------------------------------------
// 起動ログ（stderr に出す）
// ---------------------------------------------------------------------------

process.stderr.write(
    `[server.js] Node.js サイドカー起動 (PID: ${process.pid}, Node: ${process.version})\n`
);

// ---------------------------------------------------------------------------
// モジュールエクスポート（他スクリプトから register を使えるようにする）
// ---------------------------------------------------------------------------

module.exports = { register, send, resolve, reject };

// 起動完了をホストに伝えるシグナル
process.stdout.write(JSON.stringify({ ready: true }) + '\n');
