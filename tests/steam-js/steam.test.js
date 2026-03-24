// steam.test.js
// Unit tests for steam.js.
// Run with: node tests/steam-js/steam.test.js
// Exit code 0 = all passed.

'use strict';

// ---------------------------------------------------------------------------
// Minimal test framework
// ---------------------------------------------------------------------------
let pass = 0;
let fail = 0;

function expect(condition, label) {
    if (condition) {
        pass++;
    } else {
        fail++;
        const err = new Error();
        const line = (err.stack.split('\n')[2] || '').trim();
        console.error(`FAILED  ${label}  (${line})`);
    }
}

function expectEq(a, b, label) {
    const ok = JSON.stringify(a) === JSON.stringify(b);
    if (!ok) {
        fail++;
        const err = new Error();
        const line = (err.stack.split('\n')[2] || '').trim();
        console.error(`FAILED  ${label}: expected ${JSON.stringify(b)}, got ${JSON.stringify(a)}  (${line})`);
    } else {
        pass++;
    }
}

// ---------------------------------------------------------------------------
// WebView mock
// ---------------------------------------------------------------------------
class WebViewMock {
    constructor() {
        this._listeners = {};
        this.sent = [];
    }

    addEventListener(event, fn) {
        if (!this._listeners[event]) this._listeners[event] = [];
        this._listeners[event].push(fn);
    }

    postMessage(message) {
        this.sent.push(message);
    }

    // Simulate an incoming message from C#.
    // msgObj.params is a raw JS value (object/array), NOT a JSON string.
    deliver(msgObj) {
        const event = { data: JSON.stringify(msgObj) };
        for (const fn of this._listeners['message'] || []) fn(event);
    }

    lastSentRaw() { return this.sent[this.sent.length - 1]; }
    lastSent() { return JSON.parse(this.lastSentRaw()); }
    clearSent() { this.sent = []; }
}

// ---------------------------------------------------------------------------
// Window mock factory
// ---------------------------------------------------------------------------
function makeWindowMock(webviewMock) {
    const eventListeners = {};
    return {
        chrome: { webview: webviewMock },
        addEventListener(type, fn) {
            if (!eventListeners[type]) eventListeners[type] = [];
            eventListeners[type].push(fn);
        },
        dispatchEvent(e) {
            for (const fn of eventListeners[e.type] || []) fn(e);
        },
        _eventListeners: eventListeners,
    };
}

// ---------------------------------------------------------------------------
// Load steam.js fresh for each test
// ---------------------------------------------------------------------------
const fs   = require('fs');
const path = require('path');
const steamSrc = fs.readFileSync(
    path.join(__dirname, '../../web-content/steam.js'), 'utf8');

class CustomEventMock {
    constructor(type, init) {
        this.type   = type;
        this.detail = init && init.detail;
    }
}

function loadSteam(webviewMock) {
    const win = makeWindowMock(webviewMock);
    const fn  = new Function('window', 'CustomEvent', `${steamSrc}\nreturn Steam;`);
    const Steam = fn(win, CustomEventMock);
    return { Steam, win };
}

// Deliver a response and flush microtasks.
// paramsObj is a raw JS value (delivered as JSON, no double-encoding).
async function deliverResponse(mock, asyncId, paramsObj) {
    mock.deliver({
        source:    'steam',
        messageId: '',
        params:    paramsObj,   // raw value, not JSON.stringify(paramsObj)
        asyncId:   asyncId,
    });
    await Promise.resolve();
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

// -- isAvailable --

function test_isAvailable_true_when_host_present() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    expect(Steam.isAvailable() === true, 'isAvailable() == true when chrome.webview present');
}

function test_isAvailable_false_without_host() {
    const win = { addEventListener: () => {}, dispatchEvent: () => {} };
    const fn  = new Function('window', 'CustomEvent', `${steamSrc}\nreturn Steam;`);
    const Steam = fn(win, CustomEventMock);
    expect(Steam.isAvailable() === false, 'isAvailable() == false without webview');
}

// -- Outgoing message format: JSON string envelope with raw params array --

function test_init_sends_correct_message() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.init();
    expect(typeof mock.lastSentRaw() === 'string', 'init message is sent as JSON string');
    const msg = mock.lastSent();
    expectEq(msg.source,    'steam', 'init source');
    expectEq(msg.messageId, 'init',  'init messageId');
    expect(typeof msg.asyncId === 'number' && msg.asyncId >= 1, 'init asyncId >= 1');
    expectEq(msg.params, [], 'init params is raw empty array');
}

function test_unlockAchievement_message_format() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.unlockAchievement('FIRST_CLEAR');
    const msg = mock.lastSent();
    expectEq(msg.source,    'steam',           'unlock source');
    expectEq(msg.messageId, 'set-achievement', 'unlock messageId');
    expectEq(msg.params,    ['FIRST_CLEAR'],   'unlock params');
}

function test_clearAchievement_message_format() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.clearAchievement('FIRST_CLEAR');
    const msg = mock.lastSent();
    expectEq(msg.messageId, 'clear-achievement', 'clearAchievement messageId');
    expectEq(msg.params,    ['FIRST_CLEAR'],     'clearAchievement params');
}

function test_showOverlay_achievements_index() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.showOverlay('achievements');
    const msg = mock.lastSent();
    expectEq(msg.messageId, 'show-overlay', 'showOverlay messageId');
    expectEq(msg.params,    [6],            'achievements -> index 6');
    expectEq(msg.asyncId,   -1,             'fire-and-forget asyncId == -1');
}

function test_showOverlay_friends_index() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.showOverlay('friends');
    expectEq(mock.lastSent().params, [0], 'friends -> index 0');
}

function test_showOverlay_community_index() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.showOverlay('community');
    expectEq(mock.lastSent().params, [1], 'community -> index 1');
}

function test_showOverlay_stats_index() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.showOverlay('stats');
    expectEq(mock.lastSent().params, [5], 'stats -> index 5');
}

function test_showOverlay_invalid_does_not_send() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    mock.clearSent();
    Steam.showOverlay('nonexistent');
    expect(mock.sent.length === 0, 'invalid overlay: no message sent');
}

function test_showOverlayURL_format() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.showOverlayURL('https://store.steampowered.com', true);
    const msg = mock.lastSent();
    expectEq(msg.messageId, 'show-overlay-url', 'showOverlayURL messageId');
    expectEq(msg.params, ['https://store.steampowered.com', true], 'showOverlayURL params');
}

function test_setRichPresence_format() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.setRichPresence('status', 'In battle');
    const msg = mock.lastSent();
    expectEq(msg.messageId, 'set-rich-presence',   'setRichPresence messageId');
    expectEq(msg.params,    ['status', 'In battle'], 'setRichPresence params');
}

function test_clearRichPresence_format() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.clearRichPresence();
    const msg = mock.lastSent();
    expectEq(msg.messageId, 'clear-rich-presence', 'clearRichPresence messageId');
    expectEq(msg.params,    [],                    'clearRichPresence params');
}

function test_checkDlcInstalled_comma_joined() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.checkDlcInstalled([123, 456]);
    const msg = mock.lastSent();
    expectEq(msg.messageId, 'is-dlc-installed', 'checkDlcInstalled messageId');
    expectEq(msg.params, ['123,456'], 'appIds comma-joined as string');
}

function test_getAuthTicketForWebApi_format() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.getAuthTicketForWebApi('myservice');
    const msg = mock.lastSent();
    expectEq(msg.messageId, 'get-auth-ticket-for-web-api', 'getAuthTicket messageId');
    expectEq(msg.params, ['myservice'], 'getAuthTicket params');
}

function test_cancelAuthTicket_format() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.cancelAuthTicket(42);
    const msg = mock.lastSent();
    expectEq(msg.messageId, 'cancel-auth-ticket', 'cancelAuthTicket messageId');
    expectEq(msg.params, [42], 'cancelAuthTicket params');
}

function test_triggerScreenshot_format() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.triggerScreenshot();
    const msg = mock.lastSent();
    expectEq(msg.messageId, 'trigger-screenshot', 'triggerScreenshot messageId');
    expectEq(msg.params, [], 'triggerScreenshot empty params');
}

// -- Incoming message format: params is a raw value (no JSON.parse needed) --

async function test_unlock_promise_resolves() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);

    const promise = Steam.unlockAchievement('ACH_TEST');
    const asyncId = mock.lastSent().asyncId;

    // C# sends params as a raw object, not a JSON string
    await deliverResponse(mock, asyncId, { isOk: true });

    const result = await promise;
    expect(result.isOk === true, 'unlockAchievement resolves with isOk: true');
}

async function test_init_promise_resolves() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);

    const promise = Steam.init();
    const asyncId = mock.lastSent().asyncId;

    await deliverResponse(mock, asyncId, {
        isAvailable:     true,
        personaName:     'TestPlayer',
        accountId:       99999,
        appId:           480,
        steamUILanguage: 'english',
    });

    const result = await promise;
    expect(result.isAvailable === true,     'init: isAvailable');
    expectEq(result.personaName, 'TestPlayer', 'init: personaName');
    expectEq(result.appId,       480,          'init: appId');
}

async function test_multiple_concurrent_async_calls() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);

    const p1 = Steam.unlockAchievement('ACH_1');
    const id1 = mock.lastSent().asyncId;
    const p2 = Steam.unlockAchievement('ACH_2');
    const id2 = mock.lastSent().asyncId;

    expect(id1 !== id2, 'concurrent calls get distinct asyncIds');

    await deliverResponse(mock, id2, { isOk: true });
    await deliverResponse(mock, id1, { isOk: false });

    const r1 = await p1;
    const r2 = await p2;
    expect(r1.isOk === false, 'p1 resolved with correct (false)');
    expect(r2.isOk === true,  'p2 resolved with correct (true)');
}

// -- Event handling --

function test_overlay_activated_event() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);

    let captured = null;
    Steam.on('on-game-overlay-activated', (detail) => { captured = detail; });

    // params is a raw object (not a JSON string)
    mock.deliver({
        source:    'steam',
        messageId: 'on-game-overlay-activated',
        params:    { isShowing: true },
        asyncId:   -1,
    });

    expect(captured !== null,           'overlay event handler called');
    expect(captured.isShowing === true, 'isShowing is true');
}

function test_dlc_installed_event() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);

    let captured = null;
    Steam.on('on-dlc-installed', (detail) => { captured = detail; });

    mock.deliver({
        source:    'steam',
        messageId: 'on-dlc-installed',
        params:    { appId: 12345 },
        asyncId:   -1,
    });

    expect(captured !== null,      'dlc event handler called');
    expectEq(captured.appId, 12345, 'appId matches');
}

function test_non_steam_messages_ignored() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    try {
        mock.deliver({ source: 'visibilityChange', state: 'hidden' });
        expect(true, 'non-steam message does not throw');
    } catch {
        expect(false, 'non-steam message threw');
    }
}

function test_malformed_json_ignored() {
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    try {
        const event = { data: 'not valid json {{{' };
        for (const fn of mock._listeners['message'] || []) fn(event);
        expect(true, 'malformed JSON does not throw');
    } catch {
        expect(false, 'malformed JSON threw');
    }
}

// -- Encoding contract --

function test_outgoing_params_is_not_string() {
    // JS must send a JSON string payload, but params inside it must remain a raw array.
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);
    Steam.unlockAchievement('TEST');
    expect(typeof mock.lastSentRaw() === 'string',
        'outgoing message payload is a JSON string');
    const msg = mock.lastSent();
    expect(Array.isArray(msg.params),
        'outgoing params is a raw array (not double-encoded string)');
    expect(typeof msg.params !== 'string',
        'outgoing params type is not string');
}

async function test_incoming_params_is_not_double_encoded() {
    // C# sends params as a raw object; JS must NOT JSON.parse it
    const mock = new WebViewMock();
    const { Steam } = loadSteam(mock);

    const promise = Steam.init();
    const asyncId = mock.lastSent().asyncId;

    // Deliver with raw object (simulates C# BuildOutgoingJson behavior)
    await deliverResponse(mock, asyncId, { isAvailable: true, personaName: 'OK' });

    const result = await promise;
    expect(result.isAvailable === true, 'isAvailable correct (no JSON.parse)');
    expectEq(result.personaName, 'OK',  'personaName correct (no JSON.parse)');
    expect(typeof result !== 'string',  'resolved value is not a raw JSON string');
}

// ---------------------------------------------------------------------------
// C# JSON helper tests (via extractParamsJson reimplemented in JS for validation)
// ---------------------------------------------------------------------------

// Reimplementation of SteamBridge.ExtractParamsJson logic for testing
function extractParamsJson(json) {
    const key = '"params":';
    const keyIdx = json.indexOf(key);
    if (keyIdx < 0) return '[]';
    let start = keyIdx + key.length;
    while (start < json.length && json[start] === ' ') start++;
    if (start >= json.length) return '[]';
    const opener = json[start];
    if (opener !== '[' && opener !== '{') return '[]';
    const closer = opener === '[' ? ']' : '}';
    let depth = 0, inStr = false;
    for (let i = start; i < json.length; i++) {
        const c = json[i];
        if (inStr) {
            if (c === '\\') i++;
            else if (c === '"') inStr = false;
        } else if (c === '"') inStr = true;
        else if (c === opener) depth++;
        else if (c === closer && --depth === 0)
            return json.substring(start, i + 1);
    }
    return '[]';
}

function test_extractParams_array() {
    const json = '{"source":"steam","messageId":"set-achievement","params":["FIRST_CLEAR"],"asyncId":1}';
    expectEq(extractParamsJson(json), '["FIRST_CLEAR"]', 'extractParams: string array');
}

function test_extractParams_empty_array() {
    const json = '{"source":"steam","messageId":"init","params":[],"asyncId":1}';
    expectEq(extractParamsJson(json), '[]', 'extractParams: empty array');
}

function test_extractParams_mixed_array() {
    const json = '{"source":"steam","params":[true,99,"hello"],"asyncId":1}';
    expectEq(extractParamsJson(json), '[true,99,"hello"]', 'extractParams: mixed types');
}

function test_extractParams_no_params_key() {
    const json = '{"source":"steam","messageId":"run-callbacks","asyncId":-1}';
    expectEq(extractParamsJson(json), '[]', 'extractParams: missing key -> []');
}

function test_extractParams_nested_array() {
    // Nested structures (rare but should parse correctly)
    const json = '{"params":[[1,2],[3,4]]}';
    expectEq(extractParamsJson(json), '[[1,2],[3,4]]', 'extractParams: nested array');
}

function test_extractParams_string_with_escaped_quote() {
    // String value containing escaped quote
    const json = '{"params":["say \\"hi\\""],"asyncId":1}';
    expectEq(extractParamsJson(json), '["say \\"hi\\""]', 'extractParams: escaped quote in string');
}

// ---------------------------------------------------------------------------
// Run all tests
// ---------------------------------------------------------------------------
async function main() {
    console.log('=== steam.js Tests ===');

    console.log('\n-- isAvailable --');
    test_isAvailable_true_when_host_present();
    test_isAvailable_false_without_host();

    console.log('\n-- Outgoing message format --');
    test_init_sends_correct_message();
    test_unlockAchievement_message_format();
    test_clearAchievement_message_format();
    test_showOverlay_achievements_index();
    test_showOverlay_friends_index();
    test_showOverlay_community_index();
    test_showOverlay_stats_index();
    test_showOverlay_invalid_does_not_send();
    test_showOverlayURL_format();
    test_setRichPresence_format();
    test_clearRichPresence_format();
    test_checkDlcInstalled_comma_joined();
    test_getAuthTicketForWebApi_format();
    test_cancelAuthTicket_format();
    test_triggerScreenshot_format();

    console.log('\n-- Async response handling --');
    await test_unlock_promise_resolves();
    await test_init_promise_resolves();
    await test_multiple_concurrent_async_calls();

    console.log('\n-- Event handling --');
    test_overlay_activated_event();
    test_dlc_installed_event();
    test_non_steam_messages_ignored();
    test_malformed_json_ignored();

    console.log('\n-- Encoding contract --');
    test_outgoing_params_is_not_string();
    await test_incoming_params_is_not_double_encoded();

    console.log('\n-- ExtractParamsJson logic --');
    test_extractParams_array();
    test_extractParams_empty_array();
    test_extractParams_mixed_array();
    test_extractParams_no_params_key();
    test_extractParams_nested_array();
    test_extractParams_string_with_escaped_quote();

    const total = pass + fail;
    console.log(`\n${pass} / ${total} passed${fail === 0 ? '  -- ALL PASSED' : `  -- ${fail} FAILED`}`);
    process.exit(fail === 0 ? 0 : 1);
}

main().catch(e => { console.error(e); process.exit(1); });
