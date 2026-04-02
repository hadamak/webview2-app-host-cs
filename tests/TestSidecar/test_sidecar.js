/**
 * test_sidecar.js — テスト用サイドカー
 *
 * GenericSidecarPlugin のテスト用に、JSON エコーと基本的なメソッドを提供する。
 * stdin から改行区切りの JSON (NDJSON) を受け取り、
 * 登録されたハンドラを呼び出して結果を stdout に書き出す。
 *
 * プロトコル（JS ↔ C# ↔ このプロセス）:
 *   C# → stdin: { "source":"TestSidecar", "messageId":"invoke",
 *                  "params":{ "className":"ClassName", "methodName":"method", "args":[...] },
 *                  "asyncId": N }
 *
 *   stdout → C#: { "source":"TestSidecar", "messageId":"invoke-result",
 *                   "result": <value>, "asyncId": N }
 *          または: { "source":"TestSidecar", "messageId":"invoke-result",
 *                   "error": "<message>", "asyncId": N }
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
    send({ source: 'TestSidecar', messageId: 'invoke-result', result, asyncId });
}

/**
 * エラーを C# へ返す。
 * @param {number} asyncId
 * @param {string} message
 */
function reject(asyncId, message) {
    send({ source: 'TestSidecar', messageId: 'invoke-result', error: String(message), asyncId });
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
            reject(asyncId, `TestSidecar: クラス '${className}' が登録されていません`);
            return;
        }

        const fn = classHandlers[methodName];
        if (typeof fn !== 'function') {
            reject(asyncId, `TestSidecar: ${className}.${methodName} が見つかりません`);
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
                    process.stderr.write(`[test_sidecar.js] dispatch error: ${err}\n`);
                });
            } catch (parseErr) {
                process.stderr.write(`[test_sidecar.js] JSON parse error: ${parseErr}\n`);
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

register('Echo', {
    /** エコーテスト: 引数をそのまま返す */
    echo(...args) {
        return args;
    },

    /** 足し算 */
    add(a, b) {
        return a + b;
    },

    /** 引き算 */
    subtract(a, b) {
        return a - b;
    },

    /** 乗算 */
    multiply(a, b) {
        return a * b;
    },

    /** 文字列結合 */
    concat(...args) {
        return args.join('');
    },

    /** 遅延エコー（非同期テスト用） */
    async delayedEcho(message, delayMs = 1000) {
        await new Promise(resolve => setTimeout(resolve, delayMs));
        return message;
    },
});

register('Test', {
    /** バージョン情報を返す。動作確認用。 */
    version() {
        return {
            node: process.version,
            platform: process.platform,
            arch: process.arch,
            pid: process.pid,
        };
    },

    /** 現在時刻を返す */
    currentTime() {
        return {
            iso: new Date().toISOString(),
            timestamp: Date.now(),
        };
    },

    /** ランダムな数値を返す */
    random() {
        return Math.random();
    },

    /** 引数の配列をソートして返す */
    sort(arr) {
        if (!Array.isArray(arr)) {
            throw new Error('引数は配列である必要があります');
        }
        return [...arr].sort((a, b) => a - b);
    },

    /** エラーを発生させる（エラーハンドリングテスト用） */
    throwError(message) {
        throw new Error(message || 'テストエラー');
    },
});

// ---------------------------------------------------------------------------
// Ready シグナル（stdout）— waitForReady: true の場合に C# 側が待機する
// ---------------------------------------------------------------------------

process.stdout.write(JSON.stringify({ ready: true }) + '\n');

// ---------------------------------------------------------------------------
// 起動ログ（stderr に出す）
// ---------------------------------------------------------------------------

process.stderr.write(
    `[test_sidecar.js] テスト用サイドカー起動 (PID: ${process.pid}, Node: ${process.version})\n`
);

// ---------------------------------------------------------------------------
// モジュールエクスポート（他スクリプトから register を使えるようにする）
// ---------------------------------------------------------------------------

module.exports = { register, send, resolve, reject };