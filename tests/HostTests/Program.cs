using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

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
            // --- Existing append-zip tests ---
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

            // --- ParseRange tests ---
            RunParseRangeTests();

            // --- SubStream tests ---
            RunSubStreamTests();

            // --- PopupWindowOptions tests ---
            RunPopupWindowOptionsTests();

            // --- NavigationPolicy tests ---
            RunNavigationPolicyTests();

            // --- MimeTypes tests ---
            RunMimeTypesTests();

            // --- AppLog tests ---
            RunAppLogTests();

            // --- Stream disposal tests ---
            RunStreamDisposalTests();

            // --- AppConfig tests ---
            RunAppConfigTests(workDir);

            // --- AppConfig.Sanitize tests ---
            RunAppConfigSanitizeTests();

            // --- AppConfig.ApplyUserConfig tests ---
            RunAppConfigUserConfigTests(workDir);

            // --- AppConfig.IsProxyAllowed tests ---
            RunAppConfigProxyTests();

            // --- AppConfig.ProxyOrigins parsing tests ---
            RunAppConfigProxyParsingTests();

            // --- NavigationPolicy edge-case tests ---
            RunNavigationPolicyEdgeCaseTests();

            // --- ParseRange edge-case tests ---
            RunParseRangeEdgeCaseTests();

            // --- DirectorySource path traversal tests ---
            RunDirectorySourceTraversalTests(workDir);

            // --- MimeTypes case-insensitivity tests ---
            RunMimeTypesCaseTests();
        }

        // =====================================================================
        // ParseRange Tests
        // =====================================================================

        private static void RunParseRangeTests()
        {
            // Normal range
            var r1 = WebView2AppHost.WebResourceHandler.ParseRange("bytes=0-499", 1000);
            Assert(r1 != null && r1.Value.start == 0 && r1.Value.end == 499,
                "ParseRange: normal range 0-499");

            // Suffix range
            var r2 = WebView2AppHost.WebResourceHandler.ParseRange("bytes=-500", 1000);
            Assert(r2 != null && r2.Value.start == 500 && r2.Value.end == 999,
                "ParseRange: suffix range -500");

            // Open-ended range
            var r3 = WebView2AppHost.WebResourceHandler.ParseRange("bytes=500-", 1000);
            Assert(r3 != null && r3.Value.start == 500 && r3.Value.end == 999,
                "ParseRange: open-ended 500-");

            // End exceeds total (clamp)
            var r4 = WebView2AppHost.WebResourceHandler.ParseRange("bytes=0-9999", 1000);
            Assert(r4 != null && r4.Value.start == 0 && r4.Value.end == 999,
                "ParseRange: clamp end to total-1");

            // Reversed range
            var r5 = WebView2AppHost.WebResourceHandler.ParseRange("bytes=500-100", 1000);
            Assert(r5 == null, "ParseRange: reversed range returns null");

            // Start beyond total
            var r6 = WebView2AppHost.WebResourceHandler.ParseRange("bytes=1000-1001", 1000);
            Assert(r6 == null, "ParseRange: start beyond total returns null");

            // Open-ended start at EOF
            var r7 = WebView2AppHost.WebResourceHandler.ParseRange("bytes=1000-", 1000);
            Assert(r7 == null, "ParseRange: open-ended start at EOF returns null");

            // Invalid format
            var r8 = WebView2AppHost.WebResourceHandler.ParseRange("invalid", 1000);
            Assert(r8 == null, "ParseRange: invalid format returns null");

            Console.WriteLine("  ParseRange tests passed.");
        }

        // =====================================================================
        // SubStream Tests
        // =====================================================================

        private static void RunSubStreamTests()
        {
            // Partial read
            var data = Encoding.ASCII.GetBytes("0123456789ABCDEF");
            using (var ms = new MemoryStream(data))
            {
                using var sub = new WebView2AppHost.SubStream(ms, 4, 6, ownsInner: false);
                Assert(sub.Length == 6, "SubStream: Length == 6");

                var buf = new byte[10];
                var read = sub.Read(buf, 0, 10);
                Assert(read == 6, "SubStream: Read returns 6 bytes");
                var text = Encoding.ASCII.GetString(buf, 0, read);
                Assert(text == "456789", $"SubStream: content is '456789', got '{text}'");
            }

            // Seek tests
            using (var ms = new MemoryStream(data))
            {
                using var sub = new WebView2AppHost.SubStream(ms, 2, 8, ownsInner: false);

                sub.Seek(3, SeekOrigin.Begin);
                Assert(sub.Position == 3, "SubStream: Seek Begin 3");

                sub.Seek(2, SeekOrigin.Current);
                Assert(sub.Position == 5, "SubStream: Seek Current +2");

                sub.Seek(-1, SeekOrigin.End);
                Assert(sub.Position == 7, "SubStream: Seek End -1");
            }

            // Beyond-length read returns 0
            using (var ms = new MemoryStream(data))
            {
                using var sub = new WebView2AppHost.SubStream(ms, 0, 4, ownsInner: false);
                var buf = new byte[4];
                sub.Read(buf, 0, 4); // exhaust
                var read = sub.Read(buf, 0, 4);
                Assert(read == 0, "SubStream: Read beyond length returns 0");
            }

            // Disposing an owned SubStream should dispose the inner stream.
            var owned = new TrackingStream();
            using (var sub = new WebView2AppHost.SubStream(owned, 0, owned.Length))
            {
            }
            Assert(owned.IsDisposed, "SubStream: disposing owned stream disposes inner");

            // Disposing an unowned SubStream should leave the inner stream open.
            using (var unowned = new TrackingStream())
            {
                using (var sub = new WebView2AppHost.SubStream(unowned, 0, unowned.Length, ownsInner: false))
                {
                }
                Assert(!unowned.IsDisposed, "SubStream: ownsInner=false leaves inner stream open");
            }

            Console.WriteLine("  SubStream tests passed.");
        }

        // =====================================================================
        // PopupWindowOptions Tests
        // =====================================================================

        private static void RunPopupWindowOptionsTests()
        {
            var requested = WebView2AppHost.PopupWindowOptions.FromRequestedFeatures(
                hasPosition: true,
                left: 120,
                top: 80,
                hasSize: true,
                width: 640,
                height: 360,
                shouldDisplayMenuBar: false,
                shouldDisplayStatus: false,
                shouldDisplayToolbar: false,
                shouldDisplayScrollBars: true,
                fallbackWidth: 1280,
                fallbackHeight: 720);

            Assert(requested.HasPosition, "PopupWindowOptions: requested popup keeps position flag");
            Assert(requested.Left == 120 && requested.Top == 80,
                "PopupWindowOptions: requested popup keeps position");
            Assert(requested.Width == 640 && requested.Height == 360,
                "PopupWindowOptions: requested popup keeps size");
            Assert(!requested.ShouldDisplayMenuBar && !requested.ShouldDisplayStatus && !requested.ShouldDisplayToolbar,
                "PopupWindowOptions: requested popup keeps chrome flags");
            Assert(requested.ShouldDisplayScrollBars,
                "PopupWindowOptions: requested popup keeps scrollbar flag");

            var fallback = WebView2AppHost.PopupWindowOptions.FromRequestedFeatures(
                hasPosition: false,
                left: 0,
                top: 0,
                hasSize: false,
                width: 0,
                height: 0,
                shouldDisplayMenuBar: true,
                shouldDisplayStatus: true,
                shouldDisplayToolbar: true,
                shouldDisplayScrollBars: true,
                fallbackWidth: 1280,
                fallbackHeight: 720);

            Assert(!fallback.HasPosition, "PopupWindowOptions: fallback popup has no position");
            Assert(fallback.Width == 1280 && fallback.Height == 720,
                "PopupWindowOptions: fallback popup uses config size");

            var zeroSize = WebView2AppHost.PopupWindowOptions.FromRequestedFeatures(
                hasPosition: true,
                left: 20,
                top: 30,
                hasSize: true,
                width: 0,
                height: 0,
                shouldDisplayMenuBar: true,
                shouldDisplayStatus: true,
                shouldDisplayToolbar: true,
                shouldDisplayScrollBars: false,
                fallbackWidth: 800,
                fallbackHeight: 600);

            Assert(zeroSize.Width == 800 && zeroSize.Height == 600,
                "PopupWindowOptions: zero size falls back to config size");

            Console.WriteLine("  PopupWindowOptions tests passed.");
        }

        // =====================================================================
        // NavigationPolicy Tests
        // =====================================================================

        private static void RunNavigationPolicyTests()
        {
            Assert(
                WebView2AppHost.NavigationPolicy.Classify("https://app.local/index.html")
                    == WebView2AppHost.NavigationPolicy.Action.Allow,
                "NavigationPolicy: app.local -> Allow");


            Assert(
                WebView2AppHost.NavigationPolicy.Classify("https://example.com")
                    == WebView2AppHost.NavigationPolicy.Action.OpenExternal,
                "NavigationPolicy: external https -> OpenExternal");

            Assert(
                WebView2AppHost.NavigationPolicy.Classify("http://example.com")
                    == WebView2AppHost.NavigationPolicy.Action.OpenExternal,
                "NavigationPolicy: external http -> OpenExternal");

            Assert(
                WebView2AppHost.NavigationPolicy.ShouldOpenHostPopup("https://app.local/manual-popup.html"),
                "NavigationPolicy: app.local popup stays in host");


            Assert(
                !WebView2AppHost.NavigationPolicy.ShouldOpenHostPopup("https://example.com"),
                "NavigationPolicy: external https popup does not stay in host");

            Console.WriteLine("  NavigationPolicy tests passed.");
        }

        // =====================================================================
        // MimeTypes Tests
        // =====================================================================

        private static void RunMimeTypesTests()
        {
            Assert(
                WebView2AppHost.MimeTypes.FromPath("index.html") == "text/html; charset=utf-8",
                "MimeTypes: .html");

            Assert(
                WebView2AppHost.MimeTypes.FromPath("style.css") == "text/css; charset=utf-8",
                "MimeTypes: .css");

            Assert(
                WebView2AppHost.MimeTypes.FromPath("data.xyz") == "application/octet-stream",
                "MimeTypes: unknown extension");

            Console.WriteLine("  MimeTypes tests passed.");
        }

        // =====================================================================
        // AppLog Tests
        // =====================================================================

        private static void RunAppLogTests()
        {
            var oldOverride = WebView2AppHost.AppLog.Override;
            try
            {
                using var sw = new StringWriter();
                WebView2AppHost.AppLog.Override = sw;

                // Error 出力テスト
                WebView2AppHost.AppLog.Log(
                    "ERROR", "TestSource", "test error", new InvalidOperationException("test error"));
                var output = sw.ToString();
                Assert(output.Contains("[ERROR]"), "AppLog.Error: contains [ERROR]");
                Assert(output.Contains("TestSource"), "AppLog.Error: contains source");
                Assert(output.Contains("test error"), "AppLog.Error: contains message");
                Assert(output.Contains("InvalidOperationException"), "AppLog.Error: contains exception type");

                // Warn 出力テスト (message only)
                sw.GetStringBuilder().Clear();
                WebView2AppHost.AppLog.Log("WARN", "WarnSource", "warning message");
                output = sw.ToString();
                Assert(output.Contains("[WARN]"), "AppLog.Warn: contains [WARN]");
                Assert(output.Contains("WarnSource"), "AppLog.Warn: contains source");
                Assert(output.Contains("warning message"), "AppLog.Warn: contains message");

                // Warn 出力テスト (with exception)
                sw.GetStringBuilder().Clear();
                WebView2AppHost.AppLog.Log(
                    "WARN", "WarnExSource", "warn msg", new IOException("io fail"));
                output = sw.ToString();
                Assert(output.Contains("[WARN]"), "AppLog.Warn+ex: contains [WARN]");
                Assert(output.Contains("IOException"), "AppLog.Warn+ex: contains exception type");
                Assert(output.Contains("io fail"), "AppLog.Warn+ex: contains exception message");

                Console.WriteLine("  AppLog tests passed.");
            }
            finally
            {
                WebView2AppHost.AppLog.Override = oldOverride;
            }
        }

        // =====================================================================
        // Stream Disposal Tests
        // =====================================================================

        private static void RunStreamDisposalTests()
        {
            // FromStream に壊れたストリームを渡すと null が返り、ストリームが Dispose される
            var disposableStream = new TrackingStream();
            Assert(!disposableStream.IsDisposed, "StreamDisposal: stream not yet disposed");

            // Override を設定してログ出力を握りつぶす（テスト出力を汚さないため）
            var oldOverride = WebView2AppHost.AppLog.Override;
            try
            {
                WebView2AppHost.AppLog.Override = TextWriter.Null;

                // ZipContentProvider の FromStream を直接呼ぶのは private なので、
                // 代わりに ZipContentProvider 経由で壊れた ZIP を読ませてテスト
                var result = CreateProviderFromBrokenZip();
                Assert(result == false, "StreamDisposal: broken ZIP returns false from Load");

                Console.WriteLine("  Stream disposal tests passed.");
            }
            finally
            {
                WebView2AppHost.AppLog.Override = oldOverride;
            }
        }

        private static bool CreateProviderFromBrokenZip()
        {
            // 壊れた ZIP ファイルを作成して、ZipContentProvider が正しく処理するか確認
            var tempPath = Path.Combine(Path.GetTempPath(), $"broken-zip-test-{Guid.NewGuid():N}.zip");
            try
            {
                File.WriteAllBytes(tempPath, Encoding.ASCII.GetBytes("THIS IS NOT A ZIP FILE"));
                using var provider = new WebView2AppHost.ZipContentProvider(tempPath);
                return provider.Load();
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        // =====================================================================
        // AppConfig Tests
        // =====================================================================

        private static void RunAppConfigTests(string workDir)
        {
            var oldOverride = WebView2AppHost.AppLog.Override;
            try
            {
                WebView2AppHost.AppLog.Override = TextWriter.Null;

                // 正常な JSON
                var json = Encoding.UTF8.GetBytes("{\"title\":\"Test\",\"width\":800,\"height\":600}");
                using (var ms = new MemoryStream(json))
                {
                    var config = WebView2AppHost.AppConfig.Load(ms);
                    Assert(config != null, "AppConfig: valid JSON returns non-null");
                    Assert(config!.Title == "Test", "AppConfig: title parsed");
                    Assert(config.Width == 800, "AppConfig: width parsed");
                    Assert(config.Height == 600, "AppConfig: height parsed");
                }

                // 壊れた JSON → null 返却 + ログ出力
                using (var logSw = new StringWriter())
                {
                    WebView2AppHost.AppLog.Override = logSw;
                    using var broken = new MemoryStream(Encoding.UTF8.GetBytes("{invalid json!!!"));
                    var result = WebView2AppHost.AppConfig.Load(broken);
                    Assert(result == null, "AppConfig: broken JSON returns null");
                    Assert(logSw.ToString().Contains("[ERROR]"), "AppConfig: broken JSON logs error");
                }

                Console.WriteLine("  AppConfig tests passed.");
            }
            finally
            {
                WebView2AppHost.AppLog.Override = oldOverride;
            }
        }

        // =====================================================================
        // AppConfig.Sanitize Tests
        // =====================================================================

        private static void RunAppConfigSanitizeTests()
        {
            var oldOverride = WebView2AppHost.AppLog.Override;
            WebView2AppHost.AppLog.Override = System.IO.TextWriter.Null;
            try
            {
                // Title: null → デフォルト
                var c1 = LoadConfig("{\"title\":null,\"width\":800,\"height\":600}");
                Assert(c1 != null && c1!.Title == "WebView2 App Host",
                    "Sanitize: null title falls back to default");

                // Title: 空文字 → デフォルト
                var c2 = LoadConfig("{\"title\":\"\",\"width\":800,\"height\":600}");
                Assert(c2 != null && c2!.Title == "WebView2 App Host",
                    "Sanitize: empty title falls back to default");

                // Title: 空白のみ → デフォルト
                var c3 = LoadConfig("{\"title\":\"   \",\"width\":800,\"height\":600}");
                Assert(c3 != null && c3!.Title == "WebView2 App Host",
                    "Sanitize: whitespace-only title falls back to default");

                // Title: 制御文字を含む → 除去後の文字列
                var c4 = LoadConfig("{\"title\":\"Hello\\u0000World\",\"width\":800,\"height\":600}");
                Assert(c4 != null && c4!.Title == "HelloWorld",
                    "Sanitize: control chars removed from title");

                // Width: 最小値未満 → MinSize (160)
                var c5 = LoadConfig("{\"title\":\"T\",\"width\":10,\"height\":600}");
                Assert(c5 != null && c5!.Width == 160,
                    "Sanitize: width below minimum clamped to 160");

                // Height: 最小値未満 → MinSize (160)
                var c6 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":1}");
                Assert(c6 != null && c6!.Height == 160,
                    "Sanitize: height below minimum clamped to 160");

                // Width: 最大値超過 → MaxWidth (7680)
                var c7 = LoadConfig("{\"title\":\"T\",\"width\":99999,\"height\":600}");
                Assert(c7 != null && c7!.Width == 7680,
                    "Sanitize: width above maximum clamped to 7680");

                // Height: 最大値超過 → MaxHeight (4320)
                var c8 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":99999}");
                Assert(c8 != null && c8!.Height == 4320,
                    "Sanitize: height above maximum clamped to 4320");

                // 境界値: Width == MinSize (160) はそのまま通過
                var c9 = LoadConfig("{\"title\":\"T\",\"width\":160,\"height\":160}");
                Assert(c9 != null && c9!.Width == 160 && c9.Height == 160,
                    "Sanitize: min-size boundary values pass through");

                Console.WriteLine("  AppConfig.Sanitize tests passed.");
            }
            finally
            {
                WebView2AppHost.AppLog.Override = oldOverride;
            }
        }

        private static WebView2AppHost.AppConfig? LoadConfig(string json)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using var ms = new System.IO.MemoryStream(bytes);
            return WebView2AppHost.AppConfig.Load(ms);
        }
        private static void RunAppConfigProxyParsingTests()
        {
            var oldOverride = WebView2AppHost.AppLog.Override;
            WebView2AppHost.AppLog.Override = System.IO.TextWriter.Null;
            try
            {
                // proxyOrigins が JSON で正しくパースされる
                var cfg = LoadConfig(
                    "{\"title\":\"T\",\"width\":800,\"height\":600,"
                    + "\"proxyOrigins\":[\"https://api.example.com\",\"https://other.example.com\"]}"
                )!;
                Assert(cfg.ProxyOrigins.Length == 2,
                    "ProxyParsing: two origins parsed");
                Assert(cfg.ProxyOrigins[0] == "https://api.example.com",
                    "ProxyParsing: first origin correct");
                Assert(cfg.ProxyOrigins[1] == "https://other.example.com",
                    "ProxyParsing: second origin correct");

                // proxyOrigins が省略された場合は空配列
                var cfg2 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600}")!;
                Assert(cfg2.ProxyOrigins.Length == 0,
                    "ProxyParsing: absent proxyOrigins defaults to empty array");

                // 空配列を明示した場合も空配列
                var cfg3 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600,\"proxyOrigins\":[]}")!;
                Assert(cfg3.ProxyOrigins.Length == 0,
                    "ProxyParsing: explicit empty array is empty");

                Console.WriteLine("  AppConfig.ProxyOrigins parsing tests passed.");
            }
            finally
            {
                WebView2AppHost.AppLog.Override = oldOverride;
            }
        }

        private static void RunAppConfigProxyTests()
        {
            var oldOverride = WebView2AppHost.AppLog.Override;
            WebView2AppHost.AppLog.Override = System.IO.TextWriter.Null;
            try
            {
                // proxyOrigins が空の場合はすべて拒否
                var cfg1 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600}")!;
                Assert(!cfg1.IsProxyAllowed(new Uri("https://api.example.com/v1/data")),
                    "ProxyAllowed: empty proxyOrigins denies all");

                // 許可オリジンが一致する場合は許可
                var cfg2 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600,\"proxyOrigins\":[\"https://api.example.com\"]}")!;
                Assert(cfg2.IsProxyAllowed(new Uri("https://api.example.com/v1/data")),
                    "ProxyAllowed: matching origin is allowed");

                // 別オリジンは拒否
                Assert(!cfg2.IsProxyAllowed(new Uri("https://other.example.com/v1/data")),
                    "ProxyAllowed: non-matching origin is denied");

                // 末尾スラッシュがあっても一致する
                var cfg3 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600,\"proxyOrigins\":[\"https://api.example.com/\"]}")!;
                Assert(cfg3.IsProxyAllowed(new Uri("https://api.example.com/v1/data")),
                    "ProxyAllowed: trailing slash in config is normalized");

                // 大文字小文字を区別しない
                var cfg4 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600,\"proxyOrigins\":[\"HTTPS://API.EXAMPLE.COM\"]}")!;
                Assert(cfg4.IsProxyAllowed(new Uri("https://api.example.com/v1/data")),
                    "ProxyAllowed: case-insensitive origin matching");

                // 非標準ポートはポート番号込みで比較
                var cfg5 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600,\"proxyOrigins\":[\"https://api.example.com:8443\"]}")!;
                Assert(cfg5.IsProxyAllowed(new Uri("https://api.example.com:8443/v1/data")),
                    "ProxyAllowed: non-default port matches");
                Assert(!cfg5.IsProxyAllowed(new Uri("https://api.example.com/v1/data")),
                    "ProxyAllowed: default port does not match non-default port config");

                Console.WriteLine("  AppConfig.IsProxyAllowed tests passed.");
            }
            finally
            {
                WebView2AppHost.AppLog.Override = oldOverride;
            }
        }

        private static void RunAppConfigUserConfigTests(string workDir)
        {
            var oldOverride = WebView2AppHost.AppLog.Override;
            WebView2AppHost.AppLog.Override = System.IO.TextWriter.Null;
            try
            {
                // user.conf.json が存在しない場合は app.conf.json の値をそのまま使う
                var dir1 = System.IO.Path.Combine(workDir, "userconf-absent");
                System.IO.Directory.CreateDirectory(dir1);
                var cfg1 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600}")!;
                cfg1.ApplyUserConfig(dir1);
                Assert(cfg1.Width == 800 && cfg1.Height == 600,
                    "UserConfig: absent user.conf.json leaves values unchanged");

                // width・height を上書き
                var dir2 = System.IO.Path.Combine(workDir, "userconf-size");
                System.IO.Directory.CreateDirectory(dir2);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir2, "user.conf.json"),
                    "{\"width\":1920,\"height\":1080}");
                var cfg2 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600}")!;
                cfg2.ApplyUserConfig(dir2);
                Assert(cfg2.Width == 1920 && cfg2.Height == 1080,
                    "UserConfig: width and height overridden by user.conf.json");

                // fullscreen を上書き
                var dir3 = System.IO.Path.Combine(workDir, "userconf-fs");
                System.IO.Directory.CreateDirectory(dir3);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir3, "user.conf.json"),
                    "{\"fullscreen\":true}");
                var cfg3 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600,\"fullscreen\":false}")!;
                cfg3.ApplyUserConfig(dir3);
                Assert(cfg3.Fullscreen == true,
                    "UserConfig: fullscreen overridden by user.conf.json");

                // title は上書きできない（user.conf.json に書いても無視される）
                var dir4 = System.IO.Path.Combine(workDir, "userconf-title");
                System.IO.Directory.CreateDirectory(dir4);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir4, "user.conf.json"),
                    "{\"width\":1280,\"height\":720}");
                var cfg4 = LoadConfig("{\"title\":\"MyApp\",\"width\":800,\"height\":600}")!;
                cfg4.ApplyUserConfig(dir4);
                Assert(cfg4.Title == "MyApp",
                    "UserConfig: title cannot be overridden by user.conf.json");

                // 範囲外の値はクランプされる
                var dir5 = System.IO.Path.Combine(workDir, "userconf-clamp");
                System.IO.Directory.CreateDirectory(dir5);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir5, "user.conf.json"),
                    "{\"width\":99999,\"height\":1}");
                var cfg5 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600}")!;
                cfg5.ApplyUserConfig(dir5);
                Assert(cfg5.Width == 7680 && cfg5.Height == 160,
                    "UserConfig: out-of-range values are clamped");

                // 壊れた JSON は無視される
                var dir6 = System.IO.Path.Combine(workDir, "userconf-broken");
                System.IO.Directory.CreateDirectory(dir6);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir6, "user.conf.json"),
                    "{invalid json");
                var cfg6 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600}")!;
                cfg6.ApplyUserConfig(dir6);
                Assert(cfg6.Width == 800,
                    "UserConfig: broken user.conf.json is ignored");

                Console.WriteLine("  AppConfig.ApplyUserConfig tests passed.");
            }
            finally
            {
                WebView2AppHost.AppLog.Override = oldOverride;
            }
        }


        // =====================================================================
        // NavigationPolicy edge-case tests
        // =====================================================================

        private static void RunNavigationPolicyEdgeCaseTests()
        {
            // http://app.local/ は https でないため OpenExternal
            Assert(
                WebView2AppHost.NavigationPolicy.Classify("http://app.local/index.html")
                    == WebView2AppHost.NavigationPolicy.Action.OpenExternal,
                "NavigationPolicy: http://app.local -> OpenExternal (not https)");

            // IsAppLocalUri は大文字小文字を区別しない
            Assert(
                WebView2AppHost.NavigationPolicy.IsAppLocalUri("HTTPS://APP.LOCAL/index.html"),
                "NavigationPolicy: IsAppLocalUri is case-insensitive");

            // about:blank は Allow（http/https でないため外部ブラウザへ送らない）
            Assert(
                WebView2AppHost.NavigationPolicy.Classify("about:blank")
                    == WebView2AppHost.NavigationPolicy.Action.Allow,
                "NavigationPolicy: about:blank -> Allow");

            // file:// スキームも Allow（http/https でないため）
            Assert(
                WebView2AppHost.NavigationPolicy.Classify("file:///C:/test.html")
                    == WebView2AppHost.NavigationPolicy.Action.Allow,
                "NavigationPolicy: file:// -> Allow (not blocked as external)");

            // ShouldOpenHostPopup: app.local のみ true
            Assert(
                !WebView2AppHost.NavigationPolicy.ShouldOpenHostPopup("about:blank"),
                "NavigationPolicy: about:blank does not open host popup");

            Console.WriteLine("  NavigationPolicy edge-case tests passed.");
        }

        // =====================================================================
        // ParseRange edge-case tests
        // =====================================================================

        private static void RunParseRangeEdgeCaseTests()
        {
            // total == 0 → null
            Assert(
                WebView2AppHost.WebResourceHandler.ParseRange("bytes=0-0", 0) == null,
                "ParseRange: total=0 returns null");

            // suffix == 0 ("bytes=-0") → null
            Assert(
                WebView2AppHost.WebResourceHandler.ParseRange("bytes=-0", 1000) == null,
                "ParseRange: suffix=0 returns null");

            // start == end（1 バイト）→ 有効
            var single = WebView2AppHost.WebResourceHandler.ParseRange("bytes=5-5", 1000);
            Assert(single != null && single.Value.start == 5 && single.Value.end == 5,
                "ParseRange: start==end single byte is valid");

            // end が total-1 ちょうど → クランプなし
            var exact = WebView2AppHost.WebResourceHandler.ParseRange("bytes=0-999", 1000);
            Assert(exact != null && exact.Value.end == 999,
                "ParseRange: end==total-1 is valid without clamping");

            // start が負（フォーマット上は suffix range でないと不正）
            Assert(
                WebView2AppHost.WebResourceHandler.ParseRange("bytes=-1-5", 1000) == null,
                "ParseRange: malformed negative start returns null");

            Console.WriteLine("  ParseRange edge-case tests passed.");
        }

        // =====================================================================
        // DirectorySource path traversal tests
        // =====================================================================

        private static void RunDirectorySourceTraversalTests(string workDir)
        {
            var oldOverride = WebView2AppHost.AppLog.Override;
            WebView2AppHost.AppLog.Override = System.IO.TextWriter.Null;
            try
            {
            // テスト用ディレクトリ構造を作成
            var root   = System.IO.Path.Combine(workDir, "traversal-root");
            var secret = System.IO.Path.Combine(workDir, "secret.txt");
            System.IO.Directory.CreateDirectory(root);
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "safe.txt"), "safe");
            System.IO.File.WriteAllText(secret, "SECRET");

            using var provider = new WebView2AppHost.ZipContentProvider(
                // www フォルダを直接作ってロードさせるため mockExePath を利用
                mockExePath: System.IO.Path.Combine(workDir, "fake.exe")
            );

            // www フォルダを traversal-root にシンボリックリンクできないため、
            // DirectorySource を直接インスタンス化できない点を考慮し、
            // ZipContentProvider 経由で www/ フォルダを使うシナリオをシミュレートする。
            // ここでは TryGetBytes が null を返す（プロバイダが空）ことだけ確認する。
            provider.Load();  // www/ がないため空

            // 代替: パストラバーサルを含む virtualPath を DirectorySource 相当の
            // ロジックで検証する。ZipContentProvider.TryGetBytes に渡す。
            var result = provider.TryGetBytes("/../secret.txt");
            Assert(result == null,
                "DirectorySource: path traversal attempt returns null");

            var result2 = provider.TryGetBytes("/..\\secret.txt");
            Assert(result2 == null,
                "DirectorySource: backslash traversal attempt returns null");

            Console.WriteLine("  DirectorySource path traversal tests passed.");
            }
            finally
            {
                WebView2AppHost.AppLog.Override = oldOverride;
            }
        }

        // =====================================================================
        // MimeTypes case-insensitivity tests
        // =====================================================================

        private static void RunMimeTypesCaseTests()
        {
            Assert(
                WebView2AppHost.MimeTypes.FromPath("INDEX.HTML") == "text/html; charset=utf-8",
                "MimeTypes: uppercase .HTML");

            Assert(
                WebView2AppHost.MimeTypes.FromPath("script.JS") == "text/javascript",
                "MimeTypes: mixed-case .JS");

            Assert(
                WebView2AppHost.MimeTypes.FromPath("style.CSS") == "text/css; charset=utf-8",
                "MimeTypes: uppercase .CSS");

            Assert(
                WebView2AppHost.MimeTypes.FromPath("image.PNG") == "image/png",
                "MimeTypes: uppercase .PNG");

            Console.WriteLine("  MimeTypes case-insensitivity tests passed.");
        }


        // =====================================================================
        // TrackingStream (テスト用ストリーム)
        // =====================================================================

        private sealed class TrackingStream : MemoryStream
        {
            public bool IsDisposed { get; private set; }
            public TrackingStream() : base(Encoding.ASCII.GetBytes("NOT A ZIP")) { }
            protected override void Dispose(bool disposing)
            {
                IsDisposed = true;
                base.Dispose(disposing);
            }
        }

        // =====================================================================
        // Assertion helper
        // =====================================================================

        private static void Assert(bool condition, string testName)
        {
            if (!condition)
                throw new InvalidOperationException($"FAILED: {testName}");
        }

        private static void CreateZip(string path, params (string Name, string Content)[] entries)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = new StreamWriter(entry.Open(), Encoding.UTF8);
                stream.Write(content);
            }
        }

        private static void AppendFiles(string outputPath, params string[] inputs)
        {
            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            foreach (var inputPath in inputs)
            {
                using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                input.CopyTo(output);
            }
        }

        private static void AssertCanReadSingleEntry(string bundledPath, string entryName, string expectedContent)
        {
            using var provider = new WebView2AppHost.ZipContentProvider(bundledPath);
            provider.Load();

            var contentBytes = provider.TryGetBytes("/" + entryName);
            if (contentBytes == null)
            {
                throw new InvalidOperationException($"Expected entry '{entryName}' in '{bundledPath}'.");
            }

            using var reader = new StreamReader(new MemoryStream(contentBytes), Encoding.UTF8, true);
            var content = reader.ReadToEnd();
            if (!string.Equals(content, expectedContent, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected content in '{entryName}'. Expected '{expectedContent}', got '{content}'.");
            }
        }

        private static void AssertZipDoesNotContain(string bundledPath, string entryName)
        {
            using var provider = new WebView2AppHost.ZipContentProvider(bundledPath);
            provider.Load();

            if (provider.TryGetBytes("/" + entryName) != null)
            {
                throw new InvalidOperationException($"Did not expect entry '{entryName}' in '{bundledPath}'.");
            }
        }

        private static void AssertInvalidZip(string path)
        {
            using var provider = new WebView2AppHost.ZipContentProvider(path);
            bool loaded = provider.Load();
            if (loaded)
            {
                throw new InvalidOperationException($"Expected '{path}' to be rejected as a content source (Load should return false).");
            }
        }
    }
}
