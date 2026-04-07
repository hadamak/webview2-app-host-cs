using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WebView2AppHost
{
    /// <summary>
    /// MCP クライアント（Claude Desktop 等）を stdin/stdout で繋ぐコネクター。
    ///
    /// <para>
    /// MCP → バス:
    ///   tools/call を受け取り、内部 JSON-RPC に変換して Publish（バスに流す）。
    ///   DllConnector / SidecarConnector が処理し、結果を Publish で返す。
    /// </para>
    ///
    /// <para>
    /// バス → MCP:
    ///   Deliver で他のコネクターからの応答・イベントを受け取る。
    ///   - id が一致する応答 → MCP tools/call の result として stdout に書く。
    ///   - id のないイベント → MCP 通知（plugin/event/...）として stdout に書く。
    /// </para>
    ///
    /// <para>
    /// BrowserConnector が登録されていれば、
    ///   tools/list に browser_* ツールを追加する。
    ///   tools/call の browser_* はバスを経由せず BrowserConnector を直接呼ぶ。
    /// </para>
    /// </summary>
    public sealed class McpConnector : IConnector
    {
        // -------------------------------------------------------------------
        // フィールド
        // -------------------------------------------------------------------

        private readonly TextReader  _in;
        private readonly TextWriter  _out;
        private readonly TimeSpan    _callTimeout;
        private readonly McpBridge   _bridge = new McpBridge();

        private Action<string>?      _publish;
        private IBrowserTools        _browser;   // デフォルトでバス経由のプロキシが入る

        private static readonly JavaScriptSerializer s_json =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private long _nextId = 1;
        private bool _disposed;

        // -------------------------------------------------------------------
        // コンストラクタ
        // -------------------------------------------------------------------

        public McpConnector(
            TextReader? input       = null,
            TextWriter? output      = null,
            TimeSpan    callTimeout = default)
        {
            var encoding = new UTF8Encoding(false);
            _in          = input  ?? new StreamReader(Console.OpenStandardInput(),  encoding);
            _out         = output ?? new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true };
            _callTimeout = callTimeout == default ? TimeSpan.FromSeconds(30) : callTimeout;

            // デフォルトではバス経由でブラウザ操作を試みる
            _browser = new BusBrowserTools(this);

            // バスから届いた id なしメッセージ（イベント）を MCP 通知として転送
            _bridge.UnsolicitedMessage += ForwardEventAsNotification;
        }

        /// <summary>ブラウザツールが直接利用可能な場合（同一プロセス内）に注入する。</summary>
        public void SetBrowser(IBrowserTools browser) => _browser = browser;

        // -------------------------------------------------------------------
        // IConnector
        // -------------------------------------------------------------------

        public string Name => "Mcp";

        public Action<string> Publish
        {
            set => _publish = value;
        }

        /// <summary>
        /// バスから届いたメッセージを受け取る。
        /// DllConnector / SidecarConnector の応答を McpBridge に渡し、
        /// 待機中の tools/call を完了させる。
        /// </summary>
        public void Deliver(string messageJson)
        {
            if (_disposed) return;
            _bridge.Dispatch(messageJson);
        }

        // -------------------------------------------------------------------
        // メインループ
        // -------------------------------------------------------------------

        public async Task RunAsync(CancellationToken ct = default)
        {
            AppLog.Log("INFO", "McpConnector", "MCP サーバー起動（stdio NDJSON）");

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    var readTask   = _in.ReadLineAsync();
                    var cancelTask = Task.Delay(Timeout.Infinite, ct);
                    if (await Task.WhenAny(readTask, cancelTask).ConfigureAwait(false) == cancelTask) break;
                    line = await readTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", "McpConnector.Read", ex.Message, ex);
                    break;
                }

                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                _ = Task.Run(() => HandleLineAsync(line, ct), ct);
            }

            AppLog.Log("INFO", "McpConnector", "MCP サーバー終了");
        }

        // -------------------------------------------------------------------
        // メッセージ処理
        // -------------------------------------------------------------------

        private async Task HandleLineAsync(string line, CancellationToken ct)
        {
            Dictionary<string, object>? msg;
            try { msg = s_json.Deserialize<Dictionary<string, object>>(line); }
            catch { WriteError(null, -32700, "JSON パースエラー"); return; }

            if (msg == null) return;

            msg.TryGetValue("id", out var id);
            var method = (msg.TryGetValue("method", out var mv) ? mv?.ToString() : null) ?? "";

            switch (method)
            {
                case "initialize":
                    HandleInitialize(id, msg);
                    break;
                case "initialized":
                    AppLog.Log("INFO", "McpConnector", "initialized 受信");
                    break;
                case "ping":
                    WriteResult(id, new Dictionary<string, object>());
                    break;
                case "tools/list":
                    HandleToolsList(id);
                    break;
                case "tools/call":
                    await HandleToolsCallAsync(id, msg, ct).ConfigureAwait(false);
                    break;
                default:
                    if (id != null) WriteError(id, -32601, $"メソッドが見つかりません: {method}");
                    break;
            }
        }

        // -------------------------------------------------------------------
        // initialize
        // -------------------------------------------------------------------

        private void HandleInitialize(object? id, Dictionary<string, object> msg)
        {
            msg.TryGetValue("params", out var pv);
            var p = pv as Dictionary<string, object>;
            var ver = p != null && p.TryGetValue("protocolVersion", out var v) ? v?.ToString() : "?";
            AppLog.Log("INFO", "McpConnector", $"initialize (clientVersion={ver})");

            WriteResult(id, new Dictionary<string, object>
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"]    = new Dictionary<string, object>
                {
                    ["tools"] = new Dictionary<string, object> { ["listChanged"] = false },
                },
                ["serverInfo"] = new Dictionary<string, object>
                {
                    ["name"] = "WebView2AppHost MCP", ["version"] = "2.0.0",
                },
            });
        }

        // -------------------------------------------------------------------
        // tools/list
        // -------------------------------------------------------------------

        private void HandleToolsList(object? id)
        {
            var tools = new System.Collections.ArrayList
            {
                BuildToolDef("plugin_call",
                    "DLL / サイドカーのメソッドを呼び出す。\n" +
                    "method 形式: 'Alias.ClassName.MethodName'",
                    new Dictionary<string, object>
                    {
                        ["method"] = Prop("string", "呼び出すメソッド名（例: 'SQLite.Database.QueryAll'）"),
                        ["params"] = new Dictionary<string, object> { ["description"] = "引数（省略可）" },
                    },
                    required: new[] { "method" }),
            };

            // ブラウザツールを追加
            tools.Add(BuildToolDef("browser_evaluate",
                "ページの JS を実行して DOM を読み書きする。",
                new Dictionary<string, object>
                {
                    ["script"] = Prop("string", "実行する JS（例: 'document.title'）"),
                },
                required: new[] { "script" }));

            tools.Add(BuildToolDef("browser_screenshot",
                "現在の画面を PNG でキャプチャする。AI が画面を「見る」ときに使う。",
                new Dictionary<string, object>(), required: null));

            tools.Add(BuildToolDef("browser_navigate",
                "指定 URL にナビゲートし、読み込み完了を待つ。",
                new Dictionary<string, object>
                {
                    ["url"] = Prop("string", "ナビゲート先 URL"),
                },
                required: new[] { "url" }));

            tools.Add(BuildToolDef("browser_get_url",
                "現在の URL を返す。",
                new Dictionary<string, object>(), required: null));

            tools.Add(BuildToolDef("browser_get_content",
                "現在のページ全体の HTML を返す（DOM の現在状態）。",
                new Dictionary<string, object>(), required: null));

            WriteResult(id, new Dictionary<string, object> { ["tools"] = tools });
        }

        private static Dictionary<string, object> BuildToolDef(
            string name, string description,
            Dictionary<string, object> properties,
            string[]? required)
        {
            var schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties,
            };
            if (required != null) schema["required"] = required;

            return new Dictionary<string, object>
            {
                ["name"]        = name,
                ["description"] = description,
                ["inputSchema"] = schema,
            };
        }

        private static Dictionary<string, object> Prop(string type, string desc) =>
            new Dictionary<string, object> { ["type"] = type, ["description"] = desc };

        // -------------------------------------------------------------------
        // tools/call
        // -------------------------------------------------------------------

        private async Task HandleToolsCallAsync(
            object? id, Dictionary<string, object> msg, CancellationToken ct)
        {
            msg.TryGetValue("params", out var pv);
            var callParams = pv as Dictionary<string, object>;

            var toolName = callParams != null && callParams.TryGetValue("name", out var tn)
                ? tn?.ToString() : null;

            var args = callParams != null && callParams.TryGetValue("arguments", out var av)
                ? av as Dictionary<string, object> : null;

            // ブラウザツール
            if (toolName != null && toolName.StartsWith("browser_"))
            {
                await HandleBrowserToolAsync(id, toolName, args, ct).ConfigureAwait(false);
                return;
            }

            // plugin_call
            if (toolName != "plugin_call")
            {
                WriteToolError(id, $"未知のツール: {toolName}");
                return;
            }

            var pluginMethod = args != null && args.TryGetValue("method", out var pm)
                ? pm?.ToString() : null;

            if (string.IsNullOrEmpty(pluginMethod))
            {
                WriteToolError(id, "'method' が指定されていません。");
                return;
            }

            var pluginParams = args != null && args.TryGetValue("params", out var pp) ? pp : null;
            var callId = $"mcp-{Interlocked.Increment(ref _nextId)}";

            var request = s_json.Serialize(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"]      = callId,
                ["method"]  = pluginMethod,
                ["params"]  = pluginParams ?? (object)Array.Empty<object>(),
            });

            string responseJson;
            try
            {
                responseJson = await _bridge.CallAsync(
                    request, callId, _publish!, _callTimeout, ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)         { WriteToolError(id, ex.Message); return; }
            catch (OperationCanceledException)  { WriteToolError(id, "キャンセルされました。"); return; }
            catch (Exception ex)                { WriteToolError(id, ex.Message); return; }

            // プラグイン応答を MCP result に変換
            string? pluginError = null;
            object? pluginResult = null;
            try
            {
                var resp = s_json.Deserialize<Dictionary<string, object>>(responseJson);
                if (resp != null)
                {
                    if (resp.TryGetValue("result", out var r)) pluginResult = r;
                    else if (resp.TryGetValue("error", out var e))
                    {
                        var ed = e as Dictionary<string, object>;
                        pluginError = ed != null && ed.TryGetValue("message", out var em)
                            ? em?.ToString() : e?.ToString();
                    }
                }
            }
            catch { pluginResult = responseJson; }

            if (pluginError != null) { WriteToolError(id, pluginError); return; }

            WriteToolResult(id, pluginResult is string s ? s : s_json.Serialize(pluginResult));
        }

        // -------------------------------------------------------------------
        // ブラウザツール（BrowserConnector を直接、または BusBrowserTools 経由で呼ぶ）
        // -------------------------------------------------------------------

        private async Task HandleBrowserToolAsync(
            object? id, string toolName, Dictionary<string, object>? args, CancellationToken ct)
        {
            try
            {
                switch (toolName)
                {
                    case "browser_evaluate":
                    {
                        var script = args?.TryGetValue("script", out var sv) == true
                            ? sv?.ToString() ?? "" : "";
                        if (string.IsNullOrEmpty(script))
                        { WriteToolError(id, "'script' が指定されていません。"); return; }
                        WriteToolResult(id, await _browser.EvaluateAsync(script, ct));
                        break;
                    }
                    case "browser_screenshot":
                    {
                        var (base64, width, height) = await _browser.ScreenshotAsync(ct);
                        WriteResponse(id, new Dictionary<string, object>
                        {
                            ["content"] = new object[]
                            {
                                new Dictionary<string, object>
                                    { ["type"] = "image", ["data"] = base64, ["mimeType"] = "image/png" },
                                new Dictionary<string, object>
                                    { ["type"] = "text", ["text"] = $"{width}x{height} px" },
                            },
                            ["isError"] = false,
                        });
                        break;
                    }
                    case "browser_navigate":
                    {
                        var url = args?.TryGetValue("url", out var uv) == true
                            ? uv?.ToString() ?? "" : "";
                        if (string.IsNullOrEmpty(url))
                        { WriteToolError(id, "'url' が指定されていません。"); return; }
                        await _browser.NavigateAsync(url, ct);
                        WriteToolResult(id, $"ナビゲート完了: {await _browser.GetUrlAsync(ct)}");
                        break;
                    }
                    case "browser_get_url":
                        WriteToolResult(id, await _browser.GetUrlAsync(ct));
                        break;
                    case "browser_get_content":
                        WriteToolResult(id, await _browser.GetContentAsync(ct));
                        break;
                    default:
                        WriteToolError(id, $"未知のブラウザツール: {toolName}");
                        break;
                }
            }
            catch (OperationCanceledException) { WriteToolError(id, "キャンセルされました。"); }
            catch (Exception ex) { WriteToolError(id, ex.Message); }
        }

        // -------------------------------------------------------------------
        // イベント転送（サイドカー → MCP 通知）
        // -------------------------------------------------------------------

        private void ForwardEventAsNotification(string pluginJson)
        {
            try
            {
                var msg    = s_json.Deserialize<Dictionary<string, object>>(pluginJson);
                if (msg == null) return;
                var ev     = msg.TryGetValue("event",  out var e)   ? e?.ToString() : null;
                var source = msg.TryGetValue("source", out var src) ? src?.ToString() : null;
                var parms  = msg.TryGetValue("params", out var p)   ? p : null;

                Write(new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["method"]  = $"plugin/event/{source ?? "unknown"}/{ev ?? "message"}",
                    ["params"]  = parms ?? (object)new Dictionary<string, object> { ["raw"] = pluginJson },
                });
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "McpConnector.ForwardEvent", ex.Message);
            }
        }

        // -------------------------------------------------------------------
        // 送信ヘルパー
        // -------------------------------------------------------------------

        private void WriteResult(object? id, object result)
            => Write(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

        private void WriteError(object? id, int code, string message)
            => Write(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"]      = id,
                ["error"]   = new Dictionary<string, object> { ["code"] = code, ["message"] = message },
            });

        private void WriteToolResult(object? id, string text)
            => WriteResponse(id, new Dictionary<string, object>
            {
                ["content"] = new object[]
                {
                    new Dictionary<string, object> { ["type"] = "text", ["text"] = text },
                },
                ["isError"] = false,
            });

        private void WriteToolError(object? id, string message)
        {
            AppLog.Log("WARN", "McpConnector", $"ToolError: {message}");
            WriteResponse(id, new Dictionary<string, object>
            {
                ["content"] = new object[]
                {
                    new Dictionary<string, object> { ["type"] = "text", ["text"] = message },
                },
                ["isError"] = true,
            });
        }

        private void WriteResponse(object? id, object result)
            => Write(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

        private void Write(object payload)
        {
            try
            {
                var json = s_json.Serialize(payload);
                lock (_out) _out.WriteLine(json);
            }
            catch (Exception ex) { AppLog.Log("ERROR", "McpConnector.Write", ex.Message, ex); }
        }

        // -------------------------------------------------------------------
        // IDisposable
        // -------------------------------------------------------------------

        public void Dispose() => _disposed = true;

        // -------------------------------------------------------------------
        // 内部クラス: BusBrowserTools (MessageBus 経由でブラウザ操作を行うプロキシ)
        // -------------------------------------------------------------------

        private sealed class BusBrowserTools : IBrowserTools
        {
            private readonly McpConnector _parent;
            public BusBrowserTools(McpConnector parent) => _parent = parent;

            public async Task<string> EvaluateAsync(string script, CancellationToken ct)
                => await CallAsync<string>("Browser.WebView.EvaluateAsync", new { script }, ct);

            public async Task<(string Base64, int Width, int Height)> ScreenshotAsync(CancellationToken ct)
            {
                var res = await CallAsync<Dictionary<string, object>>("Browser.WebView.ScreenshotAsync", null, ct);
                return (res["base64"].ToString(), (int)res["width"], (int)res["height"]);
            }

            public async Task NavigateAsync(string url, CancellationToken ct)
                => await CallAsync<object>("Browser.WebView.NavigateAsync", new { url }, ct);

            public async Task<string> GetUrlAsync(CancellationToken ct)
                => await CallAsync<string>("Browser.WebView.GetUrlAsync", null, ct);

            public async Task<string> GetContentAsync(CancellationToken ct)
                => await CallAsync<string>("Browser.WebView.GetContentAsync", null, ct);

            private async Task<T> CallAsync<T>(string method, object? args, CancellationToken ct)
            {
                var callId = $"mcp-browser-{Interlocked.Increment(ref _parent._nextId)}";
                var request = s_json.Serialize(new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"]      = callId,
                    ["method"]  = method,
                    ["params"]  = args ?? (object)Array.Empty<object>(),
                });

                var responseJson = await _parent._bridge.CallAsync(
                    request, callId, _parent._publish!, _parent._callTimeout, ct)
                    .ConfigureAwait(false);

                var resp = s_json.Deserialize<Dictionary<string, object>>(responseJson);
                if (resp != null && resp.TryGetValue("error", out var errObj) && errObj != null)
                {
                    var ed = errObj as Dictionary<string, object>;
                    var msg = ed != null && ed.TryGetValue("message", out var em) ? em?.ToString() : errObj.ToString();
                    throw new Exception(msg);
                }

                if (resp != null && resp.TryGetValue("result", out var res))
                {
                    return (T)res;
                }
                throw new Exception("不正な応答形式です。");
            }
        }
    }
}
