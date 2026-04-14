using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Collections.Concurrent;

namespace WebView2AppHost
{
    /// <summary>
    /// Model Context Protocol (MCP) サーバーとして動作するコネクター。
    ///
    /// <para>
    /// stdin/stdout を介して MCP クライアント（AI エージェント等）と JSON-RPC 2.0 で通信し、
    /// バスに登録された DLL・サイドカー・ブラウザの機能を MCP ツールとして公開する。
    /// </para>
    ///
    /// <para>
    /// 動作モード:
    /// <list type="bullet">
    ///   <item><b>--mcp</b>: WebView2 と同居し <see cref="IBrowserTools"/> も提供する通常モード。</item>
    ///   <item><b>--mcp-headless</b>: WebView2 なしで DLL/サイドカーのみ公開するヘッドレスモード。</item>
    ///   <item><b>--mcp-proxy</b>: Named Pipe 経由で本体バスに中継する軽量プロキシモード。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>バスとの関係:</b> <see cref="McpBridge"/> を仲介役として使い、
    /// MCP からの呼び出しをバス経由で各コネクターへ転送し、応答を受け取る。
    /// コネクターが自発的に送ったイベント通知は <c>plugin/event/*</c> 形式で MCP クライアントへ転送する。
    /// </para>
    /// </summary>
    public sealed class McpConnector : IConnector
    {
        private readonly TextReader  _in;
        private readonly TextWriter  _out;
        private readonly TimeSpan    _callTimeout;
        private readonly McpBridge   _bridge = new McpBridge();
        private readonly AppConfig   _config;
        private Action<string>?      _publish;
        private IBrowserTools        _browser;
        private bool                 _browserEnabled;
        private static readonly JavaScriptSerializer s_json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private long _nextId = 1;
        private bool _disposed;
        private readonly bool _ownsIn;
        private readonly bool _ownsOut;

        /// <summary>
        /// このサーバーが準拠する MCP 仕様のバージョン（日付形式）。
        /// https://spec.modelcontextprotocol.io/ の最新リビジョンに合わせて更新する。
        /// アプリケーションのバージョンとは独立している。
        /// </summary>
        private const string McpProtocolVersion = "2024-11-05";

        private static readonly string s_hostName =
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Name
            ?? "WebView2AppHost";

        private static readonly string s_hostVersion = ResolveHostVersion();

        private static string ResolveHostVersion()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();

            var attrs = (System.Reflection.AssemblyInformationalVersionAttribute[])
                asm.GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute),
                    inherit: false);

            if (attrs.Length > 0 && !string.IsNullOrWhiteSpace(attrs[0].InformationalVersion))
                return attrs[0].InformationalVersion;

            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        public McpConnector(AppConfig? config, TextReader? input = null, TextWriter? output = null, TimeSpan callTimeout = default)
        {
            _config = config ?? new AppConfig();
            var encoding = new UTF8Encoding(false);
            _ownsIn = input == null;
            _in = input ?? new StreamReader(Console.OpenStandardInput(), encoding);
            _ownsOut = output == null;
            _out = output ?? new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true };
            _callTimeout = callTimeout == default ? TimeSpan.FromSeconds(30) : callTimeout;
            _browser = new BusBrowserTools(this);
            _bridge.UnsolicitedMessage += ForwardEventAsNotification;
        }

        public void SetBrowser(IBrowserTools browser)
        {
            _browser = browser;
            _browserEnabled = true;
        }

        public void EnableBrowserProxy() => _browserEnabled = true;

        public string Name => "Mcp";

        public Action<string> Publish
        {
            set => _publish = value;
        }

        public void Deliver(string json, Dictionary<string, object>? dict)
        {
            if (!_disposed)
            {
                _bridge.Dispatch(json, dict);
            }
        }

        /// <summary>
        /// stdin からの JSON-RPC 2.0 メッセージを読み続けるメインループ。
        /// キャンセルされるか stdin が EOF になるまで動作する。
        /// 各リクエストは独立した <see cref="Task"/> で非同期処理される。
        /// </summary>
        /// <param name="ct">シャットダウン時にキャンセルされるトークン。</param>
        public async Task RunAsync(CancellationToken ct = default)
        {
            AppLog.Log(AppLog.LogLevel.Info, "McpConnector", "MCP サーバー起動");
            var tasks = new ConcurrentQueue<Task>();
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    var readTask = _in.ReadLineAsync();
                    if (await Task.WhenAny(readTask, Task.Delay(-1, ct)) != readTask) break;
                    line = await readTask;
                }
                catch { break; }
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var task = Task.Run(async () =>
                {
                    try { await HandleLineAsync(line, ct); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { AppLog.Log(AppLog.LogLevel.Error, "McpConnector", ex.Message); }
                }, ct);
                tasks.Enqueue(task);
            }
            try { await Task.WhenAll(tasks.ToArray()).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        private async Task HandleLineAsync(string line, CancellationToken ct)
        {
            Dictionary<string, object>? msg;
            try {
                msg = s_json.Deserialize<Dictionary<string, object>>(line);
            } catch {
                WriteError(null, -32700, "JSON パースエラー");
                return;
            }
            if (msg == null) return;

            msg.TryGetValue("id", out var id);
            var method = msg.ContainsKey("method") ? msg["method"]?.ToString() : "";
            switch (method)
            {
                case "initialize":
                    WriteResult(id, new
                    {
                        protocolVersion = McpProtocolVersion,
                        capabilities    = new { tools = new { listChanged = false } },
                        serverInfo      = new
                        {
                            name    = s_hostName,
                            version = s_hostVersion
                        }
                    });
                    break;
                case "tools/list":
                    HandleToolsList(id);
                    break;
                case "tools/call":
                    await HandleToolsCallAsync(id, msg, ct);
                    break;
                case "ping":
                    WriteResult(id, new { });
                    break;
                default:
                    if (id != null)
                    {
                        WriteError(id, -32601, $"メソッドが見つかりません: {method}");
                    }
                    break;
            }
        }

        private void HandleToolsList(object? id)
        {
            var tools = new System.Collections.ArrayList();
            if (_browserEnabled)
            {
                tools.Add(BuildToolDef("browser_evaluate", "Run JS", new { script = Prop("string", "JS") }, new[] { "script" }));
                tools.Add(BuildToolDef("browser_screenshot", "Take Screenshot", new { }, Array.Empty<string>()));
                tools.Add(BuildToolDef("browser_navigate", "Navigate", new { url = Prop("string", "URL") }, new[] { "url" }));
                tools.Add(BuildToolDef("browser_click", "Click", new { selector = Prop("string", "Selector") }, new[] { "selector" }));
                tools.Add(BuildToolDef("browser_type", "Type", 
                    new { selector = Prop("string", "Selector"), text = Prop("string", "Text") }, 
                    new[] { "selector", "text" }));
                tools.Add(BuildToolDef("browser_scroll", "Scroll", 
                    new { x = Prop("number", "X"), y = Prop("number", "Y") }, 
                    new[] { "x", "y" }));
                tools.Add(BuildToolDef("browser_get_url", "Get URL", new { }, Array.Empty<string>()));
                tools.Add(BuildToolDef("browser_get_content", "Get HTML", new { }, Array.Empty<string>()));
                tools.Add(BuildToolDef("browser_pick_folder", "Open folder picker dialog", new { }, Array.Empty<string>()));
            }

            foreach (var dll in _config.LoadDlls)
            {
                tools.Add(BuildToolDef($"invoke_dll_{dll.Alias}", "DLL", 
                    new { method = Prop("string", "M"), args = new { type = "array" } }, 
                    new[] { "method" }));
            }

            foreach (var sc in _config.Sidecars)
            {
                tools.Add(BuildToolDef($"call_sidecar_{sc.Alias}", "Sidecar", 
                    new { method = Prop("string", "M"), @params = new { type = "object" } }, 
                    new[] { "method" }));
            }

            WriteResult(id, new { tools });
        }

        private static object BuildToolDef(string name, string desc, object props, string[]? req)
        {
            return new
            {
                name,
                description = desc,
                inputSchema = new { type = "object", properties = props, required = req ?? Array.Empty<string>() }
            };
        }

        private static object Prop(string type, string desc)
        {
            return new { type, description = desc };
        }

        /// <summary>
        /// tools/call リクエストを解析し、対象ツール種別（browser_* / invoke_dll_* / call_sidecar_*）へ振り分ける。
        /// </summary>
        /// <param name="id">JSON-RPC リクエスト ID。応答の id フィールドに使用する。</param>
        /// <param name="msg">デシリアライズ済みのリクエスト辞書。</param>
        /// <param name="ct">シャットダウン用キャンセルトークン。</param>
        private async Task HandleToolsCallAsync(object? id, Dictionary<string, object> msg, CancellationToken ct)
        {
            var p = msg.ContainsKey("params") ? msg["params"] as Dictionary<string, object> : null;
            var name = p?.ContainsKey("name") == true ? p["name"]?.ToString() : null;
            var args = p?.ContainsKey("arguments") == true ? p["arguments"] as Dictionary<string, object> : null;

            if (string.IsNullOrEmpty(name))
            {
                WriteToolError(id, "ツール名が指定されていません。");
                return;
            }

            if (name!.StartsWith("browser_"))
            {
                await HandleBrowserToolCallAsync(id, name, args, ct);
            }
            else if (name.StartsWith("invoke_dll_") || name.StartsWith("call_sidecar_"))
            {
                await HandlePluginToolCallAsync(id, name, args, ct);
            }
            else
            {
                WriteToolError(id, $"未知のツール: {name}");
            }
        }

        private async Task HandleBrowserToolCallAsync(object? id, string name, Dictionary<string, object>? args, CancellationToken ct)
        {
            if (!_browserEnabled)
            {
                WriteToolError(id, "ブラウザ操作ツールは現在無効です。--mcp を付けて起動してください。");
                return;
            }

            try
            {
                switch (name)
                {
                    case "browser_evaluate":
                        var s = args?.ContainsKey("script") == true ? args["script"]?.ToString() : null;
                        if (string.IsNullOrEmpty(s))
                        {
                            WriteToolError(id, "script is required");
                            return;
                        }
                        WriteToolResult(id, await _browser.EvaluateAsync(s!, ct));
                        break;

                    case "browser_screenshot":
                        var sc = await _browser.ScreenshotAsync(ct);
                        WriteToolResponse(id, new object[] 
                        { 
                            new { type = "image", data = sc.base64, mimeType = "image/png" }, 
                            new { type = "text", text = $"{sc.width}x{sc.height}px" } 
                        });
                        break;

                    case "browser_navigate":
                        await _browser.NavigateAsync(args?["url"].ToString()!, ct);
                        WriteToolResult(id, "Navigated.");
                        break;

                    case "browser_click":
                        await _browser.ClickAsync(args?["selector"].ToString()!, ct);
                        WriteToolResult(id, "Clicked.");
                        break;

                    case "browser_type":
                        await _browser.TypeAsync(args?["selector"].ToString()!, args?["text"].ToString()!, ct);
                        WriteToolResult(id, "Typed.");
                        break;

                    case "browser_scroll":
                        await _browser.ScrollAsync(Convert.ToInt32(args?["x"]), Convert.ToInt32(args?["y"]), ct);
                        WriteToolResult(id, "Scrolled.");
                        break;

                    case "browser_get_url":
                        WriteToolResult(id, await _browser.GetUrlAsync(ct));
                        break;

                    case "browser_get_content":
                        WriteToolResult(id, await _browser.GetContentAsync(ct));
                        break;
                    case "browser_pick_folder":
                        WriteToolResult(id, await _browser.PickFolderAsync(ct));
                        break;

                    default:
                        WriteToolError(id, $"未知のブラウザツール: {name}");
                        break;
                }
            }
            catch (Exception ex)
            {
                WriteToolError(id, ex.Message);
            }
        }

        private async Task HandlePluginToolCallAsync(object? id, string name, Dictionary<string, object>? args, CancellationToken ct)
        {
            var isDll = name.StartsWith("invoke_dll_");
            var alias = name.Substring(isDll ? 11 : 13);
            var method = args?.ContainsKey("method") == true ? args["method"]?.ToString() : null;
            if (string.IsNullOrEmpty(method))
            {
                WriteToolError(id, "'method' が指定されていません。");
                return;
            }
            var full = method!.Contains(".") ? method : alias + "." + method;
            var param = args?.ContainsKey(isDll ? "args" : "params") == true 
                ? args[isDll ? "args" : "params"] 
                : (isDll ? (object)new object[0] : new Dictionary<string, object>());
            await ExecutePluginCall(id, full, param, ct);
        }

        private async Task ExecutePluginCall(object? mcpId, string method, object? parms, CancellationToken ct)
        {
            var callId = $"mcp-{Interlocked.Increment(ref _nextId)}";
            var req = s_json.Serialize(new { jsonrpc = "2.0", id = callId, method, @params = parms });

            try
            {
                var res = await _bridge.CallAsync(req, callId, _publish!, _callTimeout, ct);
                var resp = s_json.Deserialize<Dictionary<string, object>>(res);

                if (resp != null && resp.ContainsKey("error"))
                {
                    WriteToolError(mcpId, s_json.Serialize(resp["error"]));
                    return;
                }

                var result = resp != null && resp.ContainsKey("result") 
                    ? (resp["result"] is string s ? s : s_json.Serialize(resp["result"])) 
                    : res;

                WriteToolResult(mcpId, result);
            }
            catch (Exception ex)
            {
                WriteToolError(mcpId, ex.Message);
            }
        }

        private void ForwardEventAsNotification(string json)
        {
            try
            {
                var msg = s_json.Deserialize<Dictionary<string, object>>(json);
                if (msg == null) return;

                if (msg.ContainsKey("jsonrpc") && msg.ContainsKey("method"))
                {
                    var m = msg["method"].ToString();
                    var p = m.Split('.');
                    if (p.Length >= 2)
                    {
                        var method = $"plugin/event/{p[0]}/{string.Join(".", p.Skip(1))}";
                        var @params = msg.ContainsKey("params") ? msg["params"] : new { };
                        Write(new { jsonrpc = "2.0", method, @params });
                    }
                }
            }
            catch { }
        }

        private void WriteResult(object? id, object res) => 
            Write(new { jsonrpc = "2.0", id, result = res });

        private void WriteError(object? id, int code, string message) => 
            Write(new { jsonrpc = "2.0", id, error = new { code, message } });

        private void WriteToolResult(object? id, string text) => 
            WriteToolResponse(id, new[] { new { type = "text", text } });

        private void WriteToolError(object? id, string text) => 
            WriteToolResponse(id, new[] { new { type = "text", text } }, true);

        private void WriteToolResponse(object? id, object content, bool isError = false) => 
            Write(new { jsonrpc = "2.0", id, result = new { content, isError } });

        private void Write(object p)
        {
            try
            {
                var j = s_json.Serialize(p);
                lock (_out)
                {
                    _out.WriteLine(j);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bridge.UnsolicitedMessage -= ForwardEventAsNotification;
            if (_ownsIn) _in.Dispose();
            if (_ownsOut) _out.Dispose();
        }

        private sealed class BusBrowserTools : IBrowserTools
        {
            private readonly McpConnector _p;
            public BusBrowserTools(McpConnector p) => _p = p;
            public Task<string> EvaluateAsync(string s, CancellationToken ct) => 
                CallAsync<string>("Browser.WebView.EvaluateAsync", new[] { s }, ct);

            public async Task<ScreenshotResult> ScreenshotAsync(CancellationToken ct)
            {
                var r = await CallAsync<Dictionary<string, object>>("Browser.WebView.ScreenshotAsync", null, ct);
                return new ScreenshotResult
                {
                    base64 = r["base64"]?.ToString() ?? "",
                    width = Convert.ToInt32(r["width"]),
                    height = Convert.ToInt32(r["height"])
                };
            }

            public Task NavigateAsync(string u, CancellationToken ct) => 
                CallAsync<object>("Browser.WebView.NavigateAsync", new[] { u }, ct);

            public Task<string> GetUrlAsync(CancellationToken ct) => 
                CallAsync<string>("Browser.WebView.GetUrlAsync", null, ct);

            public Task<string> GetContentAsync(CancellationToken ct) => 
                CallAsync<string>("Browser.WebView.GetContentAsync", null, ct);

            public Task ClickAsync(string s, CancellationToken ct) => 
                CallAsync<object>("Browser.WebView.ClickAsync", new[] { s }, ct);

            public Task TypeAsync(string s, string t, CancellationToken ct) => 
                CallAsync<object>("Browser.WebView.TypeAsync", new[] { s, t }, ct);

            public Task ScrollAsync(int x, int y, CancellationToken ct) => 
                CallAsync<object>("Browser.WebView.ScrollAsync", new[] { x, y }, ct);

            public Task<string> GetElementsAsync(CancellationToken ct) => 
                CallAsync<string>("Browser.WebView.GetElementsAsync", null, ct);

            public Task ClickLabelAsync(int i, CancellationToken ct) => 
                CallAsync<object>("Browser.WebView.ClickLabelAsync", new[] { (object)i }, ct);

            public Task ClearLabelsAsync(CancellationToken ct) => 
                CallAsync<object>("Browser.WebView.ClearLabelsAsync", null, ct);

            public Task<string> PickFolderAsync(CancellationToken ct = default) =>
                CallAsync<string>("Browser.WebView.PickFolderAsync", null, ct);

            private async Task<T> CallAsync<T>(string m, object? a, CancellationToken ct)
            {
                var id = $"mcp-b-{Interlocked.Increment(ref _p._nextId)}";
                var req = s_json.Serialize(new { jsonrpc = "2.0", id, method = m, @params = a ?? new object[0] });
                
                var res = await _p._bridge.CallAsync(req, id, _p._publish!, _p._callTimeout, ct);
                var resp = s_json.Deserialize<Dictionary<string, object>>(res);

                if (resp != null && resp.ContainsKey("error"))
                {
                    throw new Exception(s_json.Serialize(resp["error"]));
                }

                return (T)(resp?["result"] ?? default(T)!);
            }
        }
    }
}
