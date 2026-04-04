/**
 * test_sidecar.js — テスト用サイドカー
 *
 * GenericSidecarPlugin のテスト用に、JSON エコーと基本的なメソッドを提供する。
 * stdin から改行区切りの JSON (NDJSON) を受け取り、
 * 登録されたハンドラを呼び出して結果を stdout に書き出す。
 *
 * プロトコル (JSON-RPC 2.0):
 *   C# → stdin: { "jsonrpc": "2.0", "id": N, "method": "TestSidecar.ClassName.Method", "params": [...] }
 *   stdout → C#: { "jsonrpc": "2.0", "id": N, "result": ... }
 *              または: { "jsonrpc": "2.0", "id": N, "error": { "code": -32000, "message": "..." } }
 */

'use strict';

// ---------------------------------------------------------------------------
// ハンドラレジストリ
// ---------------------------------------------------------------------------

const _handlers = new Map();

function register(className, methods) {
    _handlers.set(className, methods);
}

// ---------------------------------------------------------------------------
// 送受信
// ---------------------------------------------------------------------------

function send(obj) {
    process.stdout.write(JSON.stringify(obj) + '\n');
}

function resolve(id, result) {
    send({ jsonrpc: '2.0', id, result });
}

function reject(id, message) {
    send({ jsonrpc: '2.0', id, error: { code: -32000, message: String(message) } });
}

// ---------------------------------------------------------------------------
// メッセージディスパッチ
// ---------------------------------------------------------------------------

async function dispatch(msg) {
    if (msg.jsonrpc !== '2.0' || !msg.method) {
        reject(msg.id ?? 0, 'Invalid JSON-RPC 2.0 request');
        return;
    }

    const methodParts = msg.method.split('.');
    if (methodParts.length < 3) {
        reject(msg.id, `Invalid method format: ${msg.method}`);
        return;
    }

    const className = methodParts[1];
    const methodName = methodParts[2];
    const args = Array.isArray(msg.params) ? msg.params : [];
    const id = msg.id;

    try {
        const classHandlers = _handlers.get(className);
        if (!classHandlers) {
            reject(id, `TestSidecar: クラス '${className}' が登録されていません`);
            return;
        }

        const fn = classHandlers[methodName];
        if (typeof fn !== 'function') {
            reject(id, `TestSidecar: ${className}.${methodName} が見つかりません`);
            return;
        }

        const result = await fn(...args);
        resolve(id, result ?? null);
    } catch (err) {
        reject(id, err?.message ?? String(err));
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
        process.exit(0);
    });
}

// ---------------------------------------------------------------------------
// デフォルトハンドラ（組み込み）
// ---------------------------------------------------------------------------

register('Echo', {
    echo(...args) { return args; },
    add(a, b) { return a + b; },
    subtract(a, b) { return a - b; },
    multiply(a, b) { return a * b; },
    concat(...args) { return args.join(''); },
    async delayedEcho(message, delayMs = 1000) {
        await new Promise(resolve => setTimeout(resolve, delayMs));
        return message;
    },
});

register('Test', {
    version() {
        return { node: process.version, platform: process.platform, arch: process.arch, pid: process.pid };
    },
    currentTime() {
        return { iso: new Date().toISOString(), timestamp: Date.now() };
    },
    random() { return Math.random(); },
    sort(arr) {
        if (!Array.isArray(arr)) throw new Error('引数は配列である必要があります');
        return [...arr].sort((a, b) => a - b);
    },
    throwError(message) { throw new Error(message || 'テストエラー'); },
});

// ---------------------------------------------------------------------------
// 起動
// ---------------------------------------------------------------------------

process.stdout.write(JSON.stringify({ ready: true }) + '\n');

process.stderr.write(
    `[test_sidecar.js] テスト用サイドカー起動 (PID: ${process.pid}, Node: ${process.version})\n`
);

module.exports = { register, send, resolve, reject };