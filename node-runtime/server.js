/**
 * server.js — WebView2AppHost Node.js サイドカー
 *
 * C# (GenericSidecarPlugin.cs) の子プロセスとして起動される。
 * stdin から改行区切りの JSON (NDJSON) を受け取り、
 * 登録されたハンドラを呼び出して結果を stdout に書き出す。
 *
 * プロトコル (JSON-RPC 2.0):
 *   C# → stdin: { "jsonrpc": "2.0", "id": N, "method": "Node.ClassName.Method", "params": [...] }
 *   stdout → C#: { "jsonrpc": "2.0", "id": N, "result": ... }
 *              または: { "jsonrpc": "2.0", "id": N, "error": { "code": -32000, "message": "..." } }
 *
 * ハンドラの登録:
 *   require('./server').register('MyClass', {
 *     async myMethod(arg1, arg2) { return arg1 + arg2; }
 *   });
 *
 * 利用例:
 *   const result = await Host.Node.MyClass.myMethod('hello', 'world');
 */

'use strict';

// ---------------------------------------------------------------------------
// ハンドラレジストリ
// ---------------------------------------------------------------------------

/** @type {Map<string, Record<string, Function>>} className → メソッドマップ */
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
            reject(id, `NodePlugin: クラス '${className}' が登録されていません`);
            return;
        }

        const fn = classHandlers[methodName];
        if (typeof fn !== 'function') {
            reject(id, `NodePlugin: ${className}.${methodName} が見つかりません`);
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
                    process.stderr.write(`[server.js] dispatch error: ${err}\n`);
                });
            } catch (parseErr) {
                process.stderr.write(`[server.js] JSON parse error: ${parseErr}\n`);
            }
        }
    });

    process.stdin.on('end', () => {
        process.stderr.write('[server.js] stdin closed\n');
    });
}

// ---------------------------------------------------------------------------
// デフォルトハンドラ（組み込み）
// ---------------------------------------------------------------------------

register('Node', {
    version() {
        return { node: process.version, platform: process.platform, arch: process.arch };
    },
    hasModule(name) {
        try { require(name); return true; } catch { return false; }
    },
    getPackageVersion(name) {
        try { return require(`${name}/package.json`).version ?? 'unknown'; } catch { return 'not found'; }
    },
    getNodeVersion() { return process.version; },
    getPlatform() { return process.platform; },
    uptime() { return process.uptime(); },
    memoryUsage() { return process.memoryUsage(); },
    cpuUsage() { return process.cpuUsage(); }
});

// ---------------------------------------------------------------------------
// exports
// ---------------------------------------------------------------------------

module.exports = { register, send, resolve, reject };

// ---------------------------------------------------------------------------
// 起動ログ
// ---------------------------------------------------------------------------

process.stderr.write(
    `[server.js] Node.js サイドカー起動 (PID: ${process.pid}, Node: ${process.version})\n`
);

// 起動完了をホストに伝えるシグナル
process.stdout.write(JSON.stringify({ ready: true }) + '\n');
