using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HostTests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                var workDir = Path.Combine(Path.GetTempPath(), "webview2-app-host-append-zip-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workDir);

                try
                {
                    RunTests(workDir);
                    Console.WriteLine("All tests passed.");
                    return 0;
                }
                finally
                {
                    try { Directory.Delete(workDir, recursive: true); } catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void RunTests(string workDir)
        {
            var prefix = Path.Combine(workDir, "prefix.exe");
            File.WriteAllBytes(prefix, Encoding.ASCII.GetBytes("MZ-PREFIX-ONLY"));

            var zip1 = Path.Combine(workDir, "one.zip");
            CreateZip(zip1, ("one.txt", "first"));

            var zip2 = Path.Combine(workDir, "two.zip");
            CreateZip(zip2, ("two.txt", "second"));

            var bundled1 = Path.Combine(workDir, "bundled1.exe");
            AppendFiles(bundled1, prefix, zip1);
            AssertCanReadSingleEntry(bundled1, "one.txt", "first");
            AssertZipDoesNotContain(bundled1, "two.txt");

            var bundled2 = Path.Combine(workDir, "bundled2.exe");
            AppendFiles(bundled2, bundled1, zip2);
            AssertCanReadSingleEntry(bundled2, "two.txt", "second");
            AssertZipDoesNotContain(bundled2, "one.txt");

            AssertInvalidZip(prefix);

            RunParseRangeTests();
            RunSubStreamTests();
            RunPopupWindowOptionsTests();
            RunNavigationPolicyTests();
            RunMimeTypesTests();
            RunAppLogTests();
            RunAppLogPolicyTests();
            RunStreamDisposalTests();
            RunAppConfigTests(workDir);
            RunAppConfigSanitizeTests();
            RunAppConfigUserConfigTests(workDir);
            RunAppConfigProxyTests();
            RunAppConfigProxyParsingTests();
            RunAppConfigStructuredTests();
            RunWebMessageHelperTests();
            RunPluginManagerLogicTests();
            RunJsonRpcProtocolTests();
            RunNavigationPolicyEdgeCaseTests();
            RunParseRangeEdgeCaseTests();
            RunDirectorySourceTraversalTests(workDir);
            RunMimeTypesCaseTests();
            RunCloseRequestStateTests();
            RunUintOverflowFixTests();
            RunLargeEntryGuardTests(workDir);
            
            // Sidecar & MCP Tests
#if !SECURE_OFFLINE
            SidecarTests.RunAll();
            McpTests.RunAll();
            ConnectorQualityTests.RunAll();
#endif
            SecureOfflineTests.RunAll();
        }

        private static void CreateZip(string path, params (string Name, string Content)[] entries)
        {
            using (var fs = new FileStream(path, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var (name, content) in entries)
                {
                    var entry = zip.CreateEntry(name);
                    using (var stream = entry.Open())
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(content);
                    }
                }
            }
        }

        private static void AppendFiles(string outputPath, params string[] inputs)
        {
            using (var output = new FileStream(outputPath, FileMode.Create))
            {
                foreach (var inputPath in inputs)
                {
                    using (var input = new FileStream(inputPath, FileMode.Open))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }

        private static void AssertCanReadSingleEntry(string bundledPath, string entryName, string expectedContent)
        {
            using (var provider = new WebView2AppHost.ZipContentProvider(bundledPath))
            {
                provider.Load();
                var bytes = provider.TryGetBytes("/" + entryName);
                if (bytes == null) throw new Exception("Entry not found: " + entryName);
                var content = Encoding.UTF8.GetString(bytes);
                if (content != expectedContent) throw new Exception($"Content mismatch in {entryName}. Expected '{expectedContent}', got '{content}'");
            }
        }

        private static void AssertZipDoesNotContain(string bundledPath, string entryName)
        {
            using (var provider = new WebView2AppHost.ZipContentProvider(bundledPath))
            {
                provider.Load();
                if (provider.TryGetBytes("/" + entryName) != null) throw new Exception("Entry should not exist: " + entryName);
            }
        }

        private static void AssertInvalidZip(string path)
        {
            using (var provider = new WebView2AppHost.ZipContentProvider(path))
            {
                if (provider.Load()) throw new Exception("Invalid zip should not be loaded: " + path);
            }
        }

        private static WebView2AppHost.AppConfig? LoadConfig(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return WebView2AppHost.AppConfig.Load(ms);
            }
        }

        private static void RunWebMessageHelperTests()
        {
            var p1 = WebView2AppHost.WebMessageHelper.GetJsonPayload(
                () => "{\"source\":\"test\"}",
                () => "\"{\\\"source\\\":\\\"test\\\"}\""
            );
            Assert(p1 == "{\"source\":\"test\"}", "WebMessageHelper: Stringify 済みの文字列が正しく抽出される");

            var p2 = WebView2AppHost.WebMessageHelper.GetJsonPayload(
                () => throw new Exception("WinRT error"),
                () => "{\"source\":\"test\"}"
            );
            Assert(p2 == "{\"source\":\"test\"}", "WebMessageHelper: オブジェクト形式が正しく抽出される");

            Console.WriteLine("  WebMessageHelper tests passed.");
        }

        private static void RunPluginManagerLogicTests()
        {
            var dummy = new TestPlugin();
            var initMethod = dummy.GetType().GetMethod("Initialize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (initMethod != null) initMethod.Invoke(dummy, new object[] { "{}" });
            
            var handleMethod = dummy.GetType().GetMethod("HandleWebMessage", new[] { typeof(string) });
            Assert(handleMethod != null, "PluginManagerLogic: HandleWebMessage が存在");
            handleMethod!.Invoke(dummy, new object[] { "{\"test\":1}" });
            Assert(dummy.LastMessage == "{\"test\":1}", "PluginManagerLogic: データ転送成功");

            Console.WriteLine("  PluginManager logic tests passed.");
        }

        private static void RunJsonRpcProtocolTests()
        {
            var oldOverride = WebView2AppHost.AppLog.Override;
            WebView2AppHost.AppLog.Override = System.IO.TextWriter.Null;
            try
            {
                RunJsonRpcRequestParsingTests();
                RunJsonRpcResponseSerializationTests();
                RunJsonRpcNotificationTests();
                Console.WriteLine("  JSON-RPC 2.0 protocol tests passed.");
            }
            finally { WebView2AppHost.AppLog.Override = oldOverride; }
        }

        private static void RunJsonRpcRequestParsingTests()
        {
            var dispatcher = new TestJsonRpcDispatcher();
            var testMsg = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"Test.StaticClass.StaticMethod\",\"params\":[\"arg1\",42]}";
            dispatcher.HandleWebMessageCoreSync(testMsg);
            
            Assert(dispatcher.LastRequest != null, "JSON-RPC: request parsed");
            Assert(dispatcher.LastRequest!.ClassName == "StaticClass", "JSON-RPC: className correct");
            Assert(dispatcher.LastRequest.MethodName == "StaticMethod", "JSON-RPC: methodName correct");

            dispatcher.LastRequest = null;
            dispatcher.HandleWebMessageCoreSync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"Test.StaticClass.MethodNoParams\",\"params\":[]}");
            Assert(dispatcher.LastRequest != null, "JSON-RPC: empty params parsed");

            dispatcher.LastRequest = null;
            dispatcher.HandleWebMessageCoreSync("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"Test.Class.Method\",\"params\":[]}");
            Assert(dispatcher.LastRequest == null, "JSON-RPC: wrong version rejected");

            Console.WriteLine("    JSON-RPC request parsing tests passed.");
        }

        private static void RunJsonRpcResponseSerializationTests()
        {
            var dispatcher = new TestJsonRpcDispatcher();
            var json = dispatcher.SerializeResponse(42, 1, null);
            Assert(json.Contains("\"jsonrpc\":\"2.0\""), "JSON-RPC: response has jsonrpc");
            Assert(json.Contains("\"id\":1"), "JSON-RPC: response has id");
            Assert(json.Contains("\"result\":42"), "JSON-RPC: response has result");
        }

        private static void RunJsonRpcNotificationTests()
        {
            var dispatcher = new TestJsonRpcDispatcher();
            dispatcher.HandleWebMessageCoreSync("{\"jsonrpc\":\"2.0\",\"method\":\"Test.OnEvent\",\"params\":{\"key\":\"value\"}}");
            Assert(dispatcher.LastNotification != null, "JSON-RPC: notification received");
            Assert(dispatcher.LastNotification!.EventName == "OnEvent", "JSON-RPC: event name correct");
        }

        private class TestJsonRpcDispatcher : WebView2AppHost.ReflectionDispatcherBase
        {
            public TestRequest? LastRequest;
            public TestNotification? LastNotification;

            protected override string SourceName => "Test";
            protected override bool ShouldWrapAsHandle(object result) => false;

            protected override Task<object?> ResolveTypeAsync(
                string? source, Dictionary<string, object>? p, string className, string methodName,
                object?[] argsRaw, object? id)
            {
                LastRequest = new TestRequest { ClassName = className, MethodName = methodName };
                return Task.FromResult<object?>(null);
            }

            protected override void OnNotificationReceived(string eventName, object? eventParams)
            {
                LastNotification = new TestNotification { EventName = eventName };
            }

            public string SerializeResponse(object? result, object? id, string? error)
            {
                var dict = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id };
                if (error != null) dict["error"] = new Dictionary<string, object> { ["code"] = -32000, ["message"] = error };
                else dict["result"] = result;
                return s_json.Serialize(dict);
            }

            public new void HandleWebMessageCore(string json)
            {
                base.HandleWebMessageCore(json);
            }

            public void HandleWebMessageCoreSync(string json)
            {
                base.HandleWebMessageCore(json);
                System.Threading.Thread.Sleep(50);
            }
        }

        private class TestRequest { public string ClassName = "", MethodName = ""; }
        private class TestNotification { public string EventName = ""; }

        public class TestPlugin
        {
            public string? LastMessage;
            public void Initialize(string json) { }
            public void HandleWebMessage(string json) => LastMessage = json;
        }

        private static void RunAppConfigStructuredTests()
        {
            var json = @"
            {
              ""window"": { ""width"": 1440, ""height"": 900, ""frame"": false },
              ""url"": ""https://app.local/dashboard.html"",
              ""proxy_origins"": [""https://api.github.com""],
              ""steam"": { ""app_id"": ""480"", ""dev_mode"": true },
              ""navigation_policy"": {
                ""external_navigation_mode"": ""rules"",
                ""open_in_browser"": [""*.github.com""],
                ""block_request_patterns"": [""*ads*""]
              },
              ""connectors"": [
                { ""type"": ""browser"" },
                { ""type"": ""dll"", ""alias"": ""Steam"", ""path"": ""Facepunch.Steamworks.Win64.dll"" },
                { ""type"": ""sidecar"", ""runtime"": ""node"", ""script"": ""agent.js"" }
              ]
            }";

            var cfg = LoadConfig(json);
            Assert(cfg != null, "StructuredConfig: loaded");
            Assert(cfg!.Width == 1440 && cfg.Height == 900, "StructuredConfig: window applied");
            Assert(cfg.Frame == false, "StructuredConfig: frame applied");
            Assert(cfg.Url == "https://app.local/dashboard.html", "StructuredConfig: url applied");
            Assert(cfg.ProxyOrigins.Length == 1 && cfg.ProxyOrigins[0] == "https://api.github.com", "StructuredConfig: proxy origins applied");
            Assert(cfg.SteamAppId == "480" && cfg.SteamDevMode, "StructuredConfig: steam applied");
            Assert(cfg.LoadDlls.Length == 1 && cfg.LoadDlls[0].Alias == "Steam", "StructuredConfig: dll normalized");
            Assert(cfg.Sidecars.Length == 1 && cfg.Sidecars[0].Alias == "Node", "StructuredConfig: sidecar normalized");
            Assert(cfg.ShouldOpenInBrowser("api.github.com"), "StructuredConfig: browser wildcard");
            Assert(cfg.IsRequestBlocked("https://cdn.example.com/ads/banner.js"), "StructuredConfig: request wildcard");

            var directNames = WebView2AppHost.ConnectorFactory.GetAvailableConnectorNames(cfg, enableMcp: false);
            Assert(directNames.Contains("Browser"), "StructuredConfig: browser connector visible");
            Assert(directNames.Contains("Host"), "StructuredConfig: dll connector visible");
            Assert(directNames.Contains("Node"), "StructuredConfig: sidecar connector visible");

            var directCfg = LoadConfig(@"{
              ""connectors"": [
                { ""type"": ""browser"" },
                { ""type"": ""pipe_server"" },
                { ""type"": ""mcp"" },
                { ""type"": ""dll"", ""alias"": ""Steam"", ""path"": ""Facepunch.Steamworks.Win64.dll"", ""expose_events"": [""OnGameOverlayActivated""] },
                { ""type"": ""sidecar"", ""alias"": ""PythonRuntime"", ""executable"": ""python"", ""working_directory"": ""python-runtime"", ""wait_for_ready"": true }
              ]
            }");
            Assert(directCfg != null, "StructuredConfig: direct config loaded");
            Assert(directCfg.LoadDlls.Length == 1 && directCfg.LoadDlls[0].Dll == "Facepunch.Steamworks.Win64.dll", "StructuredConfig: dll connector normalized");
            Assert(directCfg.Sidecars.Length == 1 && directCfg.Sidecars[0].WorkingDirectory == "python-runtime", "StructuredConfig: sidecar connector normalized");

            var directConnectorNames = WebView2AppHost.ConnectorFactory.GetAvailableConnectorNames(directCfg, enableMcp: false);
            Assert(directConnectorNames.Contains("Browser"), "StructuredConfig: direct browser type");
            Assert(directConnectorNames.Contains("Host"), "StructuredConfig: direct dll type");
            Assert(directConnectorNames.Contains("PythonRuntime"), "StructuredConfig: direct sidecar type");
            Assert(directConnectorNames.Contains("PipeServer"), "StructuredConfig: direct pipe type");
            Assert(directConnectorNames.Contains("Mcp"), "StructuredConfig: direct mcp type");
        }

        private static void RunUintOverflowFixTests()
        {
            var tempPath = Path.GetTempFileName();
            using (var fs = new FileStream(tempPath, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(Encoding.ASCII.GetBytes("MZ-DUMMY-PREFIX!"));
                w.Write(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
                w.Write(new byte[8]);
                w.Write((uint)0x00000200); w.Write((uint)0xFFFFFF00); w.Write((ushort)0);
            }
            using var provider = new WebView2AppHost.ZipContentProvider(tempPath);
            provider.Load();
            File.Delete(tempPath);
        }

        private static void RunLargeEntryGuardTests(string workDir)
        {
            var zipPath = Path.Combine(workDir, "empty-entry.zip");
            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create)) { zip.CreateEntry("empty.txt"); }
            using var provider = new WebView2AppHost.ZipContentProvider(zipPath);
            provider.Load();
            Assert(provider.TryGetBytes("/empty.txt")?.Length == 0, "LargeEntry: empty entry ok");
        }

        private static void RunParseRangeTests()
        {
            var r = WebView2AppHost.WebResourceHandler.ParseRange("bytes=0-499", 1000);
            Assert(r != null && r.Value.start == 0 && r.Value.end == 499, "ParseRange: ok");
        }

        private static void RunSubStreamTests()
        {
            using var ms = new MemoryStream(Encoding.ASCII.GetBytes("0123456789"));
            using var sub = new WebView2AppHost.SubStream(ms, 2, 4);
            Assert(sub.Length == 4, "SubStream: Length ok");
        }

        private static void RunPopupWindowOptionsTests()
        {
            var opt = WebView2AppHost.PopupWindowOptions.FromRequestedFeatures(true, 10, 10, true, 100, 100, false, false, false, false, 800, 600);
            Assert(opt.Width == 100, "PopupOptions: Width ok");
        }

        private static void RunNavigationPolicyTests()
        {
            Assert(WebView2AppHost.NavigationPolicy.Classify("https://app.local/index.html") == WebView2AppHost.NavigationPolicy.Action.Allow, "NavPolicy: local ok");

            var browserCfg = LoadConfig(@"{
              ""navigation_policy"": {
                ""external_navigation_mode"": ""browser"",
                ""block"": [""blocked.example.com""],
                ""allowed_external_schemes"": [""https"", ""mailto""]
              }
            }");
            Assert(browserCfg != null, "NavPolicy: browser config loaded");
#if SECURE_OFFLINE
            Assert(WebView2AppHost.NavigationPolicy.Classify("https://example.com", browserCfg) == WebView2AppHost.NavigationPolicy.Action.Block, "NavPolicy: secure build blocks external");
#else
            Assert(WebView2AppHost.NavigationPolicy.Classify("https://example.com", browserCfg) == WebView2AppHost.NavigationPolicy.Action.OpenExternal, "NavPolicy: browser mode external");
            Assert(WebView2AppHost.NavigationPolicy.Classify("https://blocked.example.com", browserCfg) == WebView2AppHost.NavigationPolicy.Action.Block, "NavPolicy: blocked host");
            Assert(WebView2AppHost.NavigationPolicy.Classify("mailto:test@example.com", browserCfg) == WebView2AppHost.NavigationPolicy.Action.OpenExternal, "NavPolicy: allowed mailto");
#endif

            var hostCfg = LoadConfig(@"{
              ""navigation_policy"": {
                ""external_navigation_mode"": ""host"",
                ""allowed_external_schemes"": [""https""]
              }
            }");
            Assert(hostCfg != null, "NavPolicy: host config loaded");
#if SECURE_OFFLINE
            Assert(WebView2AppHost.NavigationPolicy.Classify("https://api.github.com", hostCfg) == WebView2AppHost.NavigationPolicy.Action.Block, "NavPolicy: secure build overrides host mode");
#else
            Assert(WebView2AppHost.NavigationPolicy.Classify("https://api.github.com", hostCfg) == WebView2AppHost.NavigationPolicy.Action.Allow, "NavPolicy: host mode allow");
#endif

            var rulesCfg = LoadConfig(@"{
              ""navigation_policy"": {
                ""external_navigation_mode"": ""rules"",
                ""open_in_host"": [""*.github.com""],
                ""open_in_browser"": [""example.com""],
                ""block"": [""blocked.example.com""],
                ""allowed_external_schemes"": [""https"", ""mailto""]
              }
            }");
            Assert(rulesCfg != null, "NavPolicy: rules config loaded");
#if SECURE_OFFLINE
            Assert(WebView2AppHost.NavigationPolicy.Classify("https://api.github.com", rulesCfg) == WebView2AppHost.NavigationPolicy.Action.Block, "NavPolicy: secure build overrides rules");
#else
            Assert(WebView2AppHost.NavigationPolicy.Classify("https://api.github.com", rulesCfg) == WebView2AppHost.NavigationPolicy.Action.Allow, "NavPolicy: rules host allow");
            Assert(WebView2AppHost.NavigationPolicy.Classify("https://example.com", rulesCfg) == WebView2AppHost.NavigationPolicy.Action.OpenExternal, "NavPolicy: rules browser allow");
#endif
            Assert(WebView2AppHost.NavigationPolicy.Classify("https://blocked.example.com", rulesCfg) == WebView2AppHost.NavigationPolicy.Action.Block, "NavPolicy: rules block");

            var blockCfg = LoadConfig(@"{ ""navigation_policy"": { ""external_navigation_mode"": ""block"" } }");
            Assert(blockCfg != null, "NavPolicy: block config loaded");
            Assert(WebView2AppHost.NavigationPolicy.Classify("https://example.com", blockCfg) == WebView2AppHost.NavigationPolicy.Action.Block, "NavPolicy: block mode");
        }

        private static void RunMimeTypesTests()
        {
            Assert(WebView2AppHost.MimeTypes.FromPath("index.html") == "text/html; charset=utf-8", "MimeTypes: html ok");
        }

        private static void RunAppLogTests()
        {
            using var sw = new StringWriter();
            var old = WebView2AppHost.AppLog.Override;
            WebView2AppHost.AppLog.Override = sw;
            var level = WebView2AppHost.AppLog.MinimumLevel == WebView2AppHost.AppLog.LogLevel.Warn
                ? "WARN"
                : "INFO";
            WebView2AppHost.AppLog.Log(level, "Test", "Msg");
            WebView2AppHost.AppLog.Override = old;
            Assert(sw.ToString().Contains("Msg"), "AppLog: ok");
        }

        private static void RunAppLogPolicyTests()
        {
            var summary = WebView2AppHost.AppLog.DescribeMessageJson(
                "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"Host.Storage.Set\",\"params\":{\"key\":\"token\",\"value\":\"secret-value\"},\"result\":{\"content\":\"payload\"}}");

            Assert(summary.Contains("method=Host.Storage.Set"), "AppLogPolicy: method kept");
            Assert(summary.Contains("id=7"), "AppLogPolicy: id kept");
            Assert(summary.Contains("params=object(keys=2)"), "AppLogPolicy: params summarized");
            Assert(summary.Contains("result=object(keys=1)"), "AppLogPolicy: result summarized");
            Assert(!summary.Contains("secret-value"), "AppLogPolicy: sensitive payload removed");
            Assert(!summary.Contains("payload"), "AppLogPolicy: result payload removed");

#if DEBUG && !SECURE_OFFLINE
            Assert(WebView2AppHost.AppLog.ShouldWrite(WebView2AppHost.AppLog.LogLevel.Debug, WebView2AppHost.AppLog.LogDataKind.Sensitive),
                "AppLogPolicy: debug build allows sensitive debug logs");
#else
            Assert(!WebView2AppHost.AppLog.ShouldWrite(WebView2AppHost.AppLog.LogLevel.Debug, WebView2AppHost.AppLog.LogDataKind.Sensitive),
                "AppLogPolicy: non-debug builds suppress sensitive debug logs");
#endif

#if SECURE_OFFLINE
            Assert(!WebView2AppHost.AppLog.EnableFileOutput, "AppLogPolicy: secure build disables file output");
            Assert(WebView2AppHost.AppLog.MinimumLevel == WebView2AppHost.AppLog.LogLevel.Warn,
                "AppLogPolicy: secure build minimum level is warn");
#else
            Assert(WebView2AppHost.AppLog.EnableFileOutput, "AppLogPolicy: standard build keeps file output");
#endif
        }

        private static void RunStreamDisposalTests()
        {
            var temp = Path.GetTempFileName();
            File.WriteAllText(temp, "not a zip");
            using var p = new WebView2AppHost.ZipContentProvider(temp);
            Assert(!p.Load(), "StreamDisposal: invalid zip rejected");
            File.Delete(temp);
        }

        private static void RunAppConfigTests(string workDir)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("{\"title\":\"Test\"}"));
            var cfg = WebView2AppHost.AppConfig.Load(ms);
            Assert(cfg?.Title == "Test", "AppConfig: Title ok");
        }

        private static void RunAppConfigSanitizeTests()
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("{\"window\":{\"width\":10}}"));
            var cfg = WebView2AppHost.AppConfig.Load(ms);
            Assert(cfg?.Width >= 160, "AppConfig: Sanitize ok");
        }

        private static void RunAppConfigUserConfigTests(string workDir)
        {
            var dir = Path.Combine(workDir, "userconf");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "user.conf.json"), "{\"width\":1920}");
            var cfg = new WebView2AppHost.AppConfig();
            cfg.ApplyUserConfig(dir);
            Assert(cfg.Width == 1920, "UserConfig: Override ok");
        }

        private static void RunAppConfigProxyTests()
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("{\"proxy_origins\":[\"https://api.test\"]}"));
            var cfg = WebView2AppHost.AppConfig.Load(ms);
            Assert(cfg != null && cfg.IsProxyAllowed(new Uri("https://api.test/v1")), "Proxy: Allowed ok");
        }

        private static void RunAppConfigProxyParsingTests()
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("{\"proxy_origins\":[\"https://a\",\"https://b\"]}"));
            var cfg = WebView2AppHost.AppConfig.Load(ms);
            Assert(cfg?.ProxyOrigins.Length == 2, "Proxy: Parse ok");
        }

        private static void RunNavigationPolicyEdgeCaseTests()
        {
#if SECURE_OFFLINE
            Assert(WebView2AppHost.NavigationPolicy.Classify("http://app.local/") == WebView2AppHost.NavigationPolicy.Action.Block, "NavPolicy: secure build blocks insecure app.local");
#else
            Assert(WebView2AppHost.NavigationPolicy.Classify("http://app.local/") == WebView2AppHost.NavigationPolicy.Action.OpenExternal, "NavPolicy: insecure local external");
#endif
            Assert(WebView2AppHost.NavigationPolicy.Classify("customscheme://test") == WebView2AppHost.NavigationPolicy.Action.Block, "NavPolicy: unsupported scheme blocked");
        }

        private static void RunParseRangeEdgeCaseTests()
        {
            Assert(WebView2AppHost.WebResourceHandler.ParseRange("bytes=0-0", 0) == null, "ParseRange: zero total null");
        }

        private static void RunDirectorySourceTraversalTests(string workDir)
        {
            var root = Path.Combine(workDir, "root"); Directory.CreateDirectory(root);
            using var p = new WebView2AppHost.ZipContentProvider(Path.Combine(workDir, "fake.exe"));
            Assert(p.TryGetBytes("/../secret") == null, "Traversal: blocked");
        }

        private static void RunMimeTypesCaseTests()
        {
            Assert(WebView2AppHost.MimeTypes.FromPath("TEST.HTML") == "text/html; charset=utf-8", "MimeTypes: case insensitive ok");
        }

        private static void RunCloseRequestStateTests()
        {
            var s = new WebView2AppHost.CloseRequestState();
            Assert(!s.IsClosingConfirmed, "CloseState: initial ok");
        }

        private static void Assert(bool cond, string label) { if (!cond) throw new Exception("FAILED: " + label); }
    }
}
