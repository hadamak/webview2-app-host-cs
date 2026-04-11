using System;
using Xunit;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using WebView2AppHost;

namespace HostTests
{
    /// <summary>
    /// McpBridge および McpConnector（stdio MCP）のユニットテスト。
    /// WebView2 への依存はなく、StringReader / StringWriter でシミュレートする。
    /// </summary>
    public class McpTests : IDisposable
    {
        private readonly TextWriter _oldLog;

        public McpTests()
        {
            _oldLog = AppLog.Override;
            AppLog.Override = TextWriter.Null;
        }

        public void Dispose()
        {
            AppLog.Override = _oldLog;
        }

        // =====================================================================
        // McpBridge
        // =====================================================================

        [Fact]
        public void McpBridgeTests()
        {
            // --- 正常系: Dispatch が CallAsync の TCS を完了させる ---
            {
                var bridge  = new McpBridge();
                var sent    = (string?)null;
                var request = @"{""jsonrpc"":""2.0"",""id"":""mcp-1"",""method"":""Node.Math.Add"",""params"":[1,2]}";
                var response= @"{""jsonrpc"":""2.0"",""id"":""mcp-1"",""result"":3}";

                var callTask = bridge.CallAsync(request, "mcp-1",
                    json => { sent = json; bridge.Dispatch(response, null); },
                    TimeSpan.FromSeconds(5));

                callTask.Wait(1000);

                Assert.True(sent == request,          "McpBridge: リクエスト JSON が sendToPlugin に渡る");
                Assert.True(callTask.IsCompleted,      "McpBridge: CallAsync が完了する");
                Assert.True(callTask.Result == response, "McpBridge: 応答 JSON が返る");
            }

            // --- 正常系: Dispatch が先に来ても CallAsync は結果を受け取る ---
            {
                var bridge   = new McpBridge();
                var response = @"{""jsonrpc"":""2.0"",""id"":""mcp-2"",""result"":""ok""}";

                var callTask = bridge.CallAsync("{}","mcp-2",
                    _ => { /* sendToPlugin は何もしない */ },
                    TimeSpan.FromSeconds(5));

                // 別スレッドから少し後に Dispatch
                Task.Delay(20).ContinueWith(_ => bridge.Dispatch(response, null));
                callTask.Wait(2000);
                Assert.True(callTask.IsCompleted && callTask.Result == response,
                    "McpBridge: 遅延 Dispatch でも結果を受け取れる");
            }

            // --- タイムアウト ---
            {
                var bridge = new McpBridge();
                var ex     = (Exception?)null;
                try
                {
                    bridge.CallAsync("{}", "mcp-timeout", _ => { }, TimeSpan.FromMilliseconds(50))
                          .Wait(500);
                }
                catch (AggregateException ae) { ex = ae.InnerException; }
                Assert.True(ex is TimeoutException, "McpBridge: タイムアウトで TimeoutException");
            }

            // --- キャンセル ---
            {
                var bridge = new McpBridge();
                var cts    = new CancellationTokenSource();
                var ex     = (Exception?)null;
                var task   = bridge.CallAsync("{}", "mcp-cancel", _ => { },
                    TimeSpan.FromSeconds(5), cts.Token);
                cts.CancelAfter(30);
                try { task.Wait(500); }
                catch (AggregateException ae) { ex = ae.InnerException; }
                Assert.True(ex is OperationCanceledException, "McpBridge: キャンセルで OperationCanceledException");
            }

            // --- id 不一致(MCP起因) → ログのみで UnsolicitedMessage には流れない ---
            {
                var bridge     = new McpBridge();
                var unsolicited= (string?)null;
                bridge.UnsolicitedMessage += json => unsolicited = json;

                var orphan = @"{""jsonrpc"":""2.0"",""id"":""mcp-no-match"",""result"":""late""}";
                bridge.Dispatch(orphan, null);
                Assert.True(unsolicited == null, "McpBridge: MCP起因のid不一致は UnsolicitedMessage に流れない");
            }

            // --- 他コネクターのリクエスト(idあり) → 無視 ---
            {
                var bridge     = new McpBridge();
                var unsolicited= (string?)null;
                bridge.UnsolicitedMessage += json => unsolicited = json;

                var otherReq = @"{""jsonrpc"":""2.0"",""id"":1,""method"":""Browser.doSomething"",""params"":[]}";
                bridge.Dispatch(otherReq, null);
                Assert.True(unsolicited == null, "McpBridge: 他コネクターのリクエストは無視される");
            }

            // --- id なし (JSON-RPC 2.0 通知) → UnsolicitedMessage イベント ---
            {
                var bridge     = new McpBridge();
                var unsolicited= (string?)null;
                bridge.UnsolicitedMessage += json => unsolicited = json;

                var evt = @"{""jsonrpc"":""2.0"",""method"":""Node.onData"",""params"":{""value"":42}}";
                bridge.Dispatch(evt, null);
                Assert.True(unsolicited == evt, "McpBridge: id なし (2.0通知) は UnsolicitedMessage に流れる");
            }

            // --- 重複 id はエラー ---
            {
                var bridge = new McpBridge();
                bridge.CallAsync("{}", "dup", _ => { }, TimeSpan.FromSeconds(5));
                var ex = (Exception?)null;
                try { bridge.CallAsync("{}", "dup", _ => { }, TimeSpan.FromSeconds(5)).Wait(100); }
                catch (InvalidOperationException ioe) { ex = ioe; }
                catch (AggregateException ae) { ex = ae.InnerException; }
                Assert.True(ex is InvalidOperationException, "McpBridge: 重複 id は InvalidOperationException");
            }

            Console.WriteLine("    McpBridge tests passed.");
        }

        // =====================================================================
        // McpConnector helpers
        // =====================================================================

        /// <summary>
        /// McpConnector を起動し、
        /// stdin に lines を送り込んで stdout の出力行を返す。
        /// </summary>
        private static List<string> RunServer(
            string[] inputLines,
            Action<string, McpConnector>? handleWebMessage = null,
            int timeoutMs = 2000,
            Action<McpConnector>? configure = null)
        {
            var input  = new StringReader(string.Join("\n", inputLines) + "\n");
            var outBuf = new StringBuilder();
            var output = new StringWriter(outBuf);

            var mcp = new McpConnector(null, input, output, callTimeout: TimeSpan.FromMilliseconds(300));
            configure?.Invoke(mcp);

            // Publish は「バスに送る」口。テストではコールバックに渡し、
            // コールバック側が必要なら mcp.Deliver(...) で応答を返す（＝バスからの応答を受信）。
            mcp.Publish = reqJson => handleWebMessage?.Invoke(reqJson, mcp);

            using var cts = new CancellationTokenSource(timeoutMs);
            mcp.RunAsync(cts.Token).Wait(timeoutMs + 500);

            var lines = new List<string>();
            foreach (var line in outBuf.ToString().Split('\n'))
            {
                var l = line.Trim();
                if (!string.IsNullOrEmpty(l)) lines.Add(l);
            }
            return lines;
        }

        private static Dictionary<string, object> ParseJson(string json)
        {
            var s = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return s.Deserialize<Dictionary<string, object>>(json)
                ?? throw new Exception($"JSON パース失敗: {json}");
        }

        // =====================================================================
        // McpConnector: initialize
        // =====================================================================

        [Fact]
        public void McpServerInitializeTests()
        {
            var lines = RunServer(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{""protocolVersion"":""2024-11-05"",""capabilities"":{},""clientInfo"":{""name"":""TestClient"",""version"":""1.0""}}}"
            });

            Assert.True(lines.Count >= 1, "McpServer.initialize: 応答が1行以上ある");

            var resp = ParseJson(lines[0]);
            Assert.True(resp.ContainsKey("result"), "McpServer.initialize: result フィールドがある");

            var result = resp["result"] as Dictionary<string, object>;
            Assert.True(result != null, "McpServer.initialize: result がオブジェクト");
            Assert.True(result!.ContainsKey("protocolVersion"), "McpServer.initialize: protocolVersion がある");
            Assert.True(result.ContainsKey("capabilities"),     "McpServer.initialize: capabilities がある");
            Assert.True(result.ContainsKey("serverInfo"),       "McpServer.initialize: serverInfo がある");

            Console.WriteLine("    McpConnector.initialize tests passed.");
        }

        // =====================================================================
        // McpConnector: tools/list
        // =====================================================================

        [Fact]
        public void McpServerToolsListTests()
        {
            var lines = RunServer(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":2,""method"":""tools/list"",""params"":{}}"
            }, configure: m => m.EnableBrowserProxy());

            Assert.True(lines.Count >= 1, "McpServer.tools/list: 応答がある");

            var resp   = ParseJson(lines[0]);
            var result = resp["result"] as Dictionary<string, object>;
            Assert.True(result != null, "McpServer.tools/list: result がある");

            var tools = result!["tools"] as System.Collections.ArrayList;
            Assert.True(tools != null && tools.Count >= 1, "McpServer.tools/list: ツールが1つ以上ある");

            var first = tools![0] as Dictionary<string, object>;
            Assert.True(first != null && first!["name"]?.ToString() == "browser_evaluate",
                "McpServer.tools/list: browser_evaluate ツールが含まれる");
            Assert.True(first.ContainsKey("inputSchema"),
                "McpServer.tools/list: inputSchema がある");

            Console.WriteLine("    McpConnector.tools/list tests passed.");
        }

        // =====================================================================
        // McpConnector: tools/call
        // =====================================================================

        [Fact]
        public void McpServerToolsCallTests()
        {
            // --- 正常系: handleWebMessage がリクエスト id で応答する ---
            {
                var lines = RunServer(
                    inputLines: new[]
                    {
                        @"{""jsonrpc"":""2.0"",""id"":3,""method"":""tools/call"",""params"":{""name"":""browser_get_url"",""arguments"":{}}}"
                    },
                    handleWebMessage: (reqJson, mcp) =>
                    {
                        var s   = new System.Web.Script.Serialization.JavaScriptSerializer();
                        var req = s.Deserialize<Dictionary<string, object>>(reqJson);
                        var id  = req?["id"]?.ToString() ?? "0";
                        var resp = $@"{{""jsonrpc"":""2.0"",""id"":""{id}"",""result"":""https://app.local/""}}";
                        mcp.Deliver(resp, null);
                    },
                    configure: m => m.EnableBrowserProxy());

                Assert.True(lines.Count >= 1, "McpServer.tools/call 正常系: 応答がある");
                var resp   = ParseJson(lines[0]);
                var result = resp["result"] as Dictionary<string, object>;
                Assert.True(result != null,                                       "McpServer.tools/call 正常系: result がある");
                Assert.True((bool)(result!["isError"]) == false,                  "McpServer.tools/call 正常系: isError=false");
                var content = result["content"] as System.Collections.ArrayList;
                Assert.True(content != null && content.Count >= 1,                "McpServer.tools/call 正常系: content がある");
            }

            // --- タイムアウト: handleWebMessage が応答しない ---
            {
                var lines = RunServer(
                    inputLines: new[]
                    {
                        @"{""jsonrpc"":""2.0"",""id"":4,""method"":""tools/call"",""params"":{""name"":""browser_evaluate"",""arguments"":{""script"":""1+1""}}}"
                    },
                    handleWebMessage: (_, __) => { /* 応答しない */ },
                    configure: m => m.EnableBrowserProxy());

                Assert.True(lines.Count >= 1, "McpServer.tools/call タイムアウト: 応答がある");
                var resp   = ParseJson(lines[0]);
                var result = resp["result"] as Dictionary<string, object>;
                Assert.True(result != null && (bool)result!["isError"] == true,
                    "McpServer.tools/call タイムアウト: isError=true");
            }

            // --- name 未指定はエラー ---
            {
                var lines = RunServer(new[]
                {
                    @"{""jsonrpc"":""2.0"",""id"":5,""method"":""tools/call"",""params"":{""arguments"":{}}}"
                });
                Assert.True(lines.Count >= 1, "McpServer.tools/call: name 未指定エラーの応答がある");
                var result = ParseJson(lines[0])["result"] as Dictionary<string, object>;
                Assert.True(result != null && (bool)result!["isError"] == true,
                    "McpServer.tools/call: name 未指定は isError=true");
            }

            // --- 未知のツール名はエラー ---
            {
                var lines = RunServer(new[]
                {
                    @"{""jsonrpc"":""2.0"",""id"":6,""method"":""tools/call"",""params"":{""name"":""unknown_tool"",""arguments"":{}}}"
                });
                Assert.True(lines.Count >= 1, "McpServer.tools/call: 未知ツールの応答がある");
                var result = ParseJson(lines[0])["result"] as Dictionary<string, object>;
                Assert.True(result != null && (bool)result!["isError"] == true,
                    "McpServer.tools/call: 未知ツールは isError=true");
            }

            Console.WriteLine("    McpServer.tools/call tests passed.");
        }

        [Fact]
        public void McpServerEventForwardingTests()
        {
            var outBuf = new StringBuilder();
            var output = new StringWriter(outBuf);
            var input = new StringReader("\n"); // すぐ EOF

            var mcp = new McpConnector(null, input, output, callTimeout: TimeSpan.FromMilliseconds(100));

            using var cts = new CancellationTokenSource(500);
            var runTask = mcp.RunAsync(cts.Token);

            // 少し待ってから id なし 2.0 通知を Dispatch → UnsolicitedMessage 発火
            System.Threading.Thread.Sleep(30);
            mcp.Deliver(@"{""jsonrpc"":""2.0"",""method"":""Node.onData"",""params"":{""value"":99}}", null);
            mcp.Deliver(@"{""jsonrpc"":""2.0"",""method"":""Browser.OnTestEvent"",""params"":{""val"":1}}", null);

            runTask.Wait(1000);

            var lines = new List<string>();
            foreach (var l in outBuf.ToString().Split('\n'))
            {
                var t = l.Trim();
                if (!string.IsNullOrEmpty(t)) lines.Add(t);
            }

            var notif1 = lines.Find(l => l.Contains("plugin/event/Node/onData"));
            Assert.True(notif1 != null, "McpServer.event: 通知1が出力される");

            var notif2 = lines.Find(l => l.Contains("plugin/event/Browser/OnTestEvent"));
            Assert.True(notif2 != null, "McpServer.event: 通知2が出力される");

            Console.WriteLine("    McpServer event forwarding tests passed.");
        }

        // =====================================================================
        // McpServer: エッジケース
        // =====================================================================

        [Fact]
        public void McpServerEdgeCaseTests()
        {
            // --- ping ---
            {
                var lines = RunServer(new[]
                {
                    @"{""jsonrpc"":""2.0"",""id"":10,""method"":""ping""}"
                });
                Assert.True(lines.Count >= 1, "McpServer.ping: 応答がある");
                var resp = ParseJson(lines[0]);
                Assert.True(resp.ContainsKey("result"), "McpServer.ping: result がある");
            }

            // --- initialized 通知（応答不要）---
            {
                var lines = RunServer(new[]
                {
                    @"{""jsonrpc"":""2.0"",""method"":""initialized""}"
                });
                // initialized に対しては何も返さない
                Assert.True(lines.Count == 0, "McpServer.initialized: 応答を返さない");
            }

            // --- 不正 JSON ---
            {
                var lines = RunServer(new[] { "not-json-at-all" });
                Assert.True(lines.Count >= 1, "McpServer: 不正 JSON にはエラー応答");
                var resp = ParseJson(lines[0]);
                Assert.True(resp.ContainsKey("error"), "McpServer: 不正 JSON は error フィールド");
                var err = resp["error"] as Dictionary<string, object>;
                Assert.True(err != null && Convert.ToInt32(err!["code"]) == -32700,
                    "McpServer: ParseError は -32700");
            }

            // --- 未知メソッド（id あり）---
            {
                var lines = RunServer(new[]
                {
                    @"{""jsonrpc"":""2.0"",""id"":99,""method"":""no/such/method""}"
                });
                Assert.True(lines.Count >= 1, "McpServer: 未知メソッドにはエラー応答");
                var resp = ParseJson(lines[0]);
                var err  = resp["error"] as Dictionary<string, object>;
                Assert.True(err != null && Convert.ToInt32(err!["code"]) == -32601,
                    "McpServer: MethodNotFound は -32601");
            }

            // --- 未知メソッド（通知: id なし）は無応答 ---
            {
                var lines = RunServer(new[]
                {
                    @"{""jsonrpc"":""2.0"",""method"":""unknown/notification""}"
                });
                Assert.True(lines.Count == 0, "McpServer: 未知通知は無応答");
            }

            Console.WriteLine("    McpServer edge case tests passed.");
        }

        // =====================================================================
        // Assert helper
        // =====================================================================

        // =====================================================================
        // ブラウザツール（BrowserContext なしの場合の動作）
        // =====================================================================

        [Fact]
        public void BrowserToolTests()
        {
            // --- BrowserContext なし（Mode 1）: ツールリストにブラウザツールが出ない ---
            {
                var lines = RunServer(new[]
                {
                    @"{""jsonrpc"":""2.0"",""id"":20,""method"":""tools/list"",""params"":{}}"
                });
                Assert.True(lines.Count >= 1, "BrowserTools (Mode1): tools/list 応答がある");
                var result = ParseJson(lines[0])["result"] as Dictionary<string, object>;
                var tools  = result!["tools"] as System.Collections.ArrayList;
                Assert.True(tools != null, "BrowserTools (Mode1): tools がある");

                // ブラウザツールが含まれていないことを確認
                bool hasBrowser = false;
                foreach (var t in tools!)
                {
                    var td = t as Dictionary<string, object>;
                    if (td?["name"]?.ToString()?.StartsWith("browser_") == true)
                        hasBrowser = true;
                }
                Assert.True(!hasBrowser, "BrowserTools (Mode1): ブラウザツールは含まれない");
            }

            // --- BrowserContext なし: browser_* を呼ぶとエラー ---
            {
                var lines = RunServer(new[]
                {
                    @"{""jsonrpc"":""2.0"",""id"":21,""method"":""tools/call"",""params"":{""name"":""browser_evaluate"",""arguments"":{""script"":""document.title""}}}"
                });
                Assert.True(lines.Count >= 1, "BrowserTools (Mode1): browser_evaluate はエラー応答");
                var result = ParseJson(lines[0])["result"] as Dictionary<string, object>;
                Assert.True(result != null && (bool)result!["isError"] == true,
                    "BrowserTools (Mode1): browser_evaluate は isError=true");
                var content = result["content"] as System.Collections.ArrayList;
                var first   = content?[0] as Dictionary<string, object>;
                Assert.True(first?["text"]?.ToString()?.Contains("--mcp") == true,
                    "BrowserTools (Mode1): エラーメッセージに --mcp の説明がある");
            }

            // --- browser_screenshot: BrowserContext なし ---
            {
                var lines = RunServer(new[]
                {
                    @"{""jsonrpc"":""2.0"",""id"":22,""method"":""tools/call"",""params"":{""name"":""browser_screenshot"",""arguments"":{}}}"
                });
                var result = ParseJson(lines[0])["result"] as Dictionary<string, object>;
                Assert.True(result != null && (bool)result!["isError"] == true,
                    "BrowserTools (Mode1): browser_screenshot は isError=true");
            }

            // --- browser_navigate: script 未指定エラー ---
            {
                var lines = RunServer(new[]
                {
                    @"{""jsonrpc"":""2.0"",""id"":23,""method"":""tools/call"",""params"":{""name"":""browser_evaluate"",""arguments"":{}}}"
                });
                // BrowserContext なしなので Mode 1 エラー（script 未指定より先にチェックされる）
                var result = ParseJson(lines[0])["result"] as Dictionary<string, object>;
                Assert.True(result != null && (bool)result!["isError"] == true,
                    "BrowserTools: browser_evaluate 引数なしはエラー");
            }

            Console.WriteLine("    Browser tool tests passed.");
        }
    }
}
