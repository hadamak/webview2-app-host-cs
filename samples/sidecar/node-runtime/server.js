/**
 * server.js — WebView2AppHost Node.js サイドカー
 * ターミナル実行機能追加版
 */

'use strict';

const fs = require('fs').promises;
const path = require('path');
const { exec } = require('child_process'); // 追加: 子プロセス実行用

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

// 2. ファイルシステム操作
register('FileSystem', {
    async listFiles(dirPath) {
        const entries = await fs.readdir(dirPath, { withFileTypes: true });
        return entries.map(e => ({ name: e.name, isDirectory: e.isDirectory() }));
    },
    async readFile(filePath) {
        return await fs.readFile(filePath, 'utf8');
    }
});

// 3. ターミナル実行機能 (新規追加)
register('Terminal', {
    execute(command) {
        return new Promise((res) => {
            // コマンドの先頭に UTF-8 への切り替えを強制する
            // & で繋ぐことで、chcp 成功後に本来のコマンドを実行
            const utf8Command = `chcp 65001 > nul && ${command}`;

            exec(utf8Command, { encoding: 'buffer', timeout: 30000 }, (error, stdout, stderr) => {
                // デコーダーを utf-8 に固定
                const decoder = new TextDecoder('utf-8');
                
                res({
                    stdout: decoder.decode(stdout),
                    stderr: decoder.decode(stderr),
                    code: error ? error.code : 0,
                    ok: !error
                });
            });
        });
    }
});

// ---------------------------------------------------------------------------
// 起動処理
// ---------------------------------------------------------------------------

process.stderr.write(`[server.js] Node.js サイドカー起動 (PID: ${process.pid})\n`);

// 起動完了をホストに通知
process.stdout.write(JSON.stringify({ ready: true }) + '\n');