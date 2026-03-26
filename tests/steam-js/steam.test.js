// steam.test.js
// Unit tests for the ES6 Proxy based generic steam.js.
// Run with: node tests/steam-js/steam.test.js
// Exit code 0 = all passed.

'use strict';

let pass = 0;
let fail = 0;
const messages = [];

function expect(condition, label) {
    if (condition) pass++;
    else {
        fail++;
        console.error(`FAILED  ${label}`);
    }
}

// Mock Webview
const mockWebview = {
    postMessage(msg) {
        messages.push(JSON.parse(msg));
    },
    addEventListener(ev, handler) { }
};

const windowMock = { chrome: { webview: mockWebview } };

const fs = require('fs');
const path = require('path');
const steamSrc = fs.readFileSync(path.join(__dirname, '../../src/steam.js'), 'utf8');

// Load steam.js
const Steam = new Function('window', `
    ${steamSrc};
    return Steam;
`)(windowMock);

async function main() {
    console.log('=== steam.js ES6 Proxy Tests ===');

    // 1. Static invocation test
    // Should trigger messageId: 'invoke', params: { className, methodName, args }
    Steam.SteamUserStats.SetStat('Wins', 5);
    let msg = messages.pop();
    expect(msg.messageId === 'invoke', 'Static generic method generates invoke messageId');
    expect(msg.params.className === 'SteamUserStats', 'Class Name is captured correctly');
    expect(msg.params.methodName === 'SetStat', 'Method Name is captured correctly');
    expect(msg.params.args.length === 2 && msg.params.args[0] === 'Wins' && msg.params.args[1] === 5, 'Arguments are transmitted clearly');

    // 2. Promise .then should not be proxied (Prevents infinite loops resolving await)
    const thenProxyResult = Steam.SteamClient.then;
    expect(thenProxyResult === undefined, '.then is ignored on proxies so await works safely');

    // 3. Simulated Host result: Proxy generation for instances
    // Because we mock postMessage, we must directly test the internal handle wrapper.
    // By simulating an incoming message `wrapHandles(msg.result)` if we modify steam.js to export it.
    // However, since steam.js is tightly scoped, we can simulate an event message that we registered a handler for!
    
    let wrappedResponse = null;
    Steam.on('TestEvent', (payload) => {
        wrappedResponse = payload;
    });

    // We manually trigger the internal listener since chrome.webview listener is bound to window.chrome.webview
    // But since `window.chrome.webview.addEventListener` didn't save the handler to our mock yet, let's inject a dispatcher:
    let bridgeCallback = null;
    const windowMockFull = {
        chrome: {
            webview: {
                postMessage(m) { messages.push(JSON.parse(m)); },
                addEventListener(ev, handler) { bridgeCallback = handler; }
            }
        }
    };
    const SteamFull = new Function('window', `${steamSrc}; return Steam;`)(windowMockFull);
    
    SteamFull.on('IncomingHandle', (params) => {
        wrappedResponse = params; // Params should be proxified
    });

    // Simulate C# sending an event with a Handle:
    bridgeCallback({
        data: JSON.stringify({
            source: 'steam',
            event: 'IncomingHandle',
            params: { __isHandle: true, __handleId: 999, className: 'Leaderboard' }
        })
    });

    expect(typeof wrappedResponse === 'object' && wrappedResponse !== null, 'Event payload is received');
    console.log("Wrapped response:", wrappedResponse, "typeof:", typeof wrappedResponse, "isHandle:", wrappedResponse.__isHandle);
    expect(typeof wrappedResponse === 'object', 'Proxy was created');
    
    // It is a proxy! Call a method on it.
    wrappedResponse.SubmitScoreAsync(100);
    msg = messages.pop();
    
    expect(msg.messageId === 'invoke', 'Instance proxy method generates invoke messageId');
    expect(msg.params.handleId === 999, 'Instance proxy captures the internal HandleId (999)');
    expect(msg.params.methodName === 'SubmitScoreAsync', 'Instance proxy method maps correctly');
    expect(msg.params.args[0] === 100, 'Instance arguments forward correctly');

    const total = pass + fail;
    console.log(`\n${pass} / ${total} passed${fail === 0 ? '  -- ALL PASSED' : `  -- ${fail} FAILED`}`);
    process.exit(fail === 0 ? 0 : 1);
}

main().catch(console.error);
