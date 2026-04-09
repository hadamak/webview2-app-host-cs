/**
 * test_sidecar.js — CUI テスト用サイドカー (修正版)
 */
'use strict';

function send(obj) {
    process.stdout.write(JSON.stringify(obj) + '\n');
}

// 起動完了通知
send({ ready: true });

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
            const { id, method, params } = msg;

            // 後方一致で判定 (Alias.Test.Math.Add に対応)
            if (method.endsWith('Test.Math.Add')) {
                send({ jsonrpc: '2.0', id, result: params[0] + params[1] });
            } else if (method.endsWith('Test.System.Echo')) {
                send({ jsonrpc: '2.0', id, result: params });
            } else if (method.endsWith('Test.Error.Throw')) {
                send({ jsonrpc: '2.0', id, error: { code: -32000, message: 'Intentional Error' } });
            } else if (method.endsWith('Test.Process.Exit')) {
                process.exit(0);
            }
        } catch (e) {
            process.stderr.write('JSON Parse Error: ' + trimmed + '\n');
        }
    }
});
