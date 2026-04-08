const readline = require('readline');
const rl = readline.createInterface({ input: process.stdin, terminal: false });

console.error('[Agent] Started');

let pendingRequests = new Map();
let nextId = 1;

rl.on('line', (line) => {
    try {
        const msg = JSON.parse(line);
        // console.error('[Agent] Received:', line);
        
        if (msg.id && !msg.method) {
            const resolve = pendingRequests.get(msg.id.toString());
            if (resolve) {
                pendingRequests.delete(msg.id.toString());
                resolve(msg);
                return;
            }
        }

        if (msg.method === 'Agent.RunTask') {
            handleRunTask(msg);
        } else if (msg.method === 'Agent.Echo') {
            send({ jsonrpc: '2.0', id: msg.id, result: { message: msg.params[0] } });
        }
    } catch (e) {
        console.error('[Agent] Error processing line:', e.message);
    }
});

async function handleRunTask(req) {
    try {
        console.error('[Agent] Handling RunTask...');
        const callId = 'agent-call-' + (nextId++);
        const hostRequest = {
            jsonrpc: '2.0',
            id: callId,
            method: 'Browser.WebView.GetUrlAsync',
            params: []
        };

        const responsePromise = new Promise((resolve, reject) => {
            const timeout = setTimeout(() => reject(new Error('Host call timeout')), 5000);
            pendingRequests.set(callId, (resp) => {
                clearTimeout(timeout);
                resolve(resp);
            });
        });

        console.error('[Agent] Sending request to host:', JSON.stringify(hostRequest));
        send(hostRequest);

        const hostResponse = await responsePromise;
        console.error('[Agent] Received response from host:', JSON.stringify(hostResponse));
        const url = hostResponse.result;

        send({
            jsonrpc: '2.0',
            id: req.id,
            result: {
                status: 'success',
                report: 'エージェントがブラウザを確認しました。現在のURLは ' + url + ' です。タスクを完了しました。'
            }
        });
    } catch (err) {
        console.error('[Agent] Task error:', err.message);
        send({
            jsonrpc: '2.0',
            id: req.id,
            error: { code: -32000, message: err.message }
        });
    }
}

function send(obj) {
    process.stdout.write(JSON.stringify(obj) + '\n');
}

send({ ready: true });
