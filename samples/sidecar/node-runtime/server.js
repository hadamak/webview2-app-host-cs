/**
 * server.js — WebView2AppHost Node.js サイドカーサンプル
 *
 * 【概要】
 * C# (GenericSidecarPlugin.cs) から子プロセスとして起動され、
 * 標準入出力 (StdIO) を介して JavaScript と JSON-RPC 2.0 形式で通信します。
 * 
 * 【通信プロトコル (NDJSON)】
 *   - 受信 (stdin): {"jsonrpc":"2.0", "id":1, "method":"Node.ClassName.MethodName", "params":[...]}
 *   - 送信 (stdout): {"jsonrpc":"2.0", "id":1, "result":...}
 *   - ログ (stderr): ホストのログに出力されます
 */

'use strict';

const fs = require('fs').promises;
const path = require('path');

// ---------------------------------------------------------------------------
// ハンドラレジストリ
// ---------------------------------------------------------------------------

const _handlers = new Map();

/**
 * クラス名とメソッドのマップを登録します。
 * @param {string} className JS からアクセスする際のクラス名
 * @param {Record<string, Function>} methods メソッドの定義
 */
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

        // メソッドの実行（async/await に対応）
        const result = await fn(...args);
        resolve(id, result ?? null);
    } catch (err) {
        process.stderr.write(`[server.js] Error in ${msg.method}: ${err}\n`);
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
                    process.stderr.write(`[server.js] dispatch fatal: ${err}\n`);
                });
            } catch (parseErr) {
                process.stderr.write(`[server.js] JSON parse error: ${parseErr}\n`);
            }
        }
    });
}

// ---------------------------------------------------------------------------
// ハンドラ登録
// ---------------------------------------------------------------------------

// 1. 基本情報
register('Node', {
    version() { return process.version; },
    platform() { return process.platform; },
    memory() { return process.memoryUsage(); }
});

// 2. ファイルシステム操作の例
register('FileSystem', {
    async listFiles(dirPath) {
        const entries = await fs.readdir(dirPath, { withFileTypes: true });
        return entries.map(e => ({ name: e.name, isDirectory: e.isDirectory() }));
    },
    async readFile(filePath) {
        return await fs.readFile(filePath, 'utf8');
    }
});

// ---------------------------------------------------------------------------
// 起動処理
// ---------------------------------------------------------------------------

process.stderr.write(`[server.js] Node.js サイドカー起動 (PID: ${process.pid})\n`);

// ホストに起動完了（Ready）を通知
// app.conf.json で waitForReady: true の場合、この受信までメッセージがキューイングされます
process.stdout.write(JSON.stringify({ ready: true }) + '\n');
