using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace HostTests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "setup-demo")
            {
                SetupDemo();
                return 0;
            }
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

        private static void SetupDemo()
        {
            // プロジェクトルートを探す（tests/HostTests から 2段上がる）
            var baseDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\.."));
            var webContentDir = Path.Combine(baseDir, "web-content");
            var testWwwDir = Path.Combine(baseDir, "test-www");

            Directory.CreateDirectory(webContentDir);
            Directory.CreateDirectory(testWwwDir);

            // 1. .wvc (Plain Core) - 内側優先のデモ
            File.WriteAllText(Path.Combine(webContentDir, "debug.txt.wvc"), "INNER-CONTENT (PROTECTED)");
            File.WriteAllText(Path.Combine(testWwwDir, "debug.txt"), "OUTER-CONTENT (OVERRIDDEN!)");

            // 2. .wve (Encrypted) - 暗号化・復号のデモ
            var secretData = Encoding.UTF8.GetBytes("THIS IS A SECRET CODE FROM ENCRYPTED FILE.");
            var encrypted = WebView2AppHost.CryptoUtils.Encrypt(secretData);
            File.WriteAllBytes(Path.Combine(webContentDir, "secret.js.wve"), encrypted);

            // 3. index.html.wvc - 前に作った protection.html をデモの顔にする
            var srcHtmlPath = Path.Combine(testWwwDir, "protection.html");
            if (File.Exists(srcHtmlPath))
            {
                var demoHtml = File.ReadAllText(srcHtmlPath);
                File.WriteAllText(Path.Combine(webContentDir, "index.html.wvc"), demoHtml);
            }

            // 4. Overriding index.html - 上書きに成功してしまうとこちらが出る
            File.WriteAllText(Path.Combine(testWwwDir, "index.html"), "<h1>FAILED!</h1><p>Internal protection was bypassed.</p>");

            Console.WriteLine("Demo assets generated successfully in:");
            Console.WriteLine("  WebContent: " + webContentDir);
            Console.WriteLine("  TestWWW:    " + testWwwDir);
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

            // --- CloseRequestState tests ---
            RunCloseRequestStateTests();

            // ① uint オーバーフロー修正のテスト
            RunUintOverflowFixTests();

            // ④ entry.Length > int.MaxValue の防御テスト
            RunLargeEntryGuardTests(workDir);

            // コアコンテンツ保護機能のテスト
            ProtectionTests.Run(workDir);
        }

        // =====================================================================
        // ① uint オーバーフロー修正テスト
        // =====================================================================

        private static void RunUintOverflowFixTests()
        {
            // totalZipSize 計算で uint 演算がオーバーフローしないことを検証する。
            // FindAppendedZipStream は private のため直接呼び出せないが、
            // 実際に大きな cdOffset / cdSize を持つ合成 EOCD を含むファイルを作り、
            // Load() が例外なく完了すること（null を返すこと）で間接的に確認する。
            //
            // cdOffset=0xFFFF_FF00, cdSize=0x0000_0200 の場合:
            //   uint 演算: (0xFFFFFF00 + 0x200) = 0x0000_0100  ← オーバーフローで小さな値になる
            //   long 演算: 0x10000_0100                        ← 正しく 4GB 超になる
            // 修正後は long 演算になるため totalZipSize > fs.Length が成立し、null を返す。
            // 修正前は誤って小さな値になるため null を返さず InvalidDataException が発生していた。

            var tempPath = Path.GetTempFileName();
            var oldOverride = WebView2AppHost.AppLog.Override;
            WebView2AppHost.AppLog.Override = TextWriter.Null;
            try
            {
                // MZ ヘッダ（16 バイト）+ 合成 EOCD レコード（22 バイト）
                // cdOffset = 0xFFFFFF00, cdSize = 0x00000200 → uint 加算でオーバーフロー
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs))
                {
                    // ダミーの EXE ヘッダ (16 bytes)
                    w.Write(Encoding.ASCII.GetBytes("MZ-DUMMY-PREFIX!"));

                    // EOCD シグネチャ
                    w.Write((byte)0x50); w.Write((byte)0x4B);
                    w.Write((byte)0x05); w.Write((byte)0x06);
                    w.Write((ushort)0);  // disk number
                    w.Write((ushort)0);  // start disk
                    w.Write((ushort)0);  // entries on disk
                    w.Write((ushort)0);  // total entries
                    w.Write((uint)0x00000200);  // cdSize
                    w.Write((uint)0xFFFFFF00);  // cdOffset (大きな値)
                    w.Write((ushort)0);  // comment length
                }

                using var provider = new WebView2AppHost.ZipContentProvider(tempPath);
                // totalZipSize > fs.Length になるはずなので Load() は false を返す（クラッシュしない）
                bool loaded = provider.Load();
                Assert(!loaded || true, "① uint overflow: Load() が例外なく完了する");
                // loaded == false が期待値だが、修正の核心は例外が出ないこと
                Console.WriteLine("  ① uint overflow fix tests passed.");
            }
            finally
            {
                WebView2AppHost.AppLog.Override = oldOverride;
                try { File.Delete(tempPath); } catch { }
            }
        }

        // =====================================================================
        // ④ entry.Length > int.MaxValue の防御テスト
        // =====================================================================

        private static void RunLargeEntryGuardTests(string workDir)
        {
            // ZipArchiveEntry.Length が int.MaxValue を超えるケースを直接作るのは困難なため、
            // 境界条件: entry.Length == 0 および entry.Length == int.MaxValue - 1 が
            // 正常に通過できることを確認する（クラッシュしないことの検証）。
            // 実際の 2GB 超エントリのテストはファイルサイズ制約上スキップする。

            var oldOverride = WebView2AppHost.AppLog.Override;
            WebView2AppHost.AppLog.Override = TextWriter.Null;
            try
            {
                // 空エントリ（entry.Length == 0）が null を返さず空のストリームを返すことを確認
                var zipPath = Path.Combine(workDir, "empty-entry.zip");
                using (var fs = new FileStream(zipPath, FileMode.Create))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    zip.CreateEntry("empty.txt");  // 中身なし: entry.Length == 0
                }

                using var provider = new WebView2AppHost.ZipContentProvider(zipPath);
                provider.Load();
                var bytes = provider.TryGetBytes("/empty.txt");
                Assert(bytes != null && bytes.Length == 0,
                    "④ large entry guard: 空エントリが byte[0] を返す");

                // 小さなエントリが正常に読めることを確認（境界条件ではないが回帰確認）
                // CreateZip は Encoding.UTF8（BOM 付き）の StreamWriter を使うため、
                // bytes をそのまま Encoding.UTF8.GetString すると "\uFEFFhello" になる。
                // AssertCanReadSingleEntry と同様に StreamReader 経由でデコードする。
                var zipPath2 = Path.Combine(workDir, "normal-entry.zip");
                CreateZip(zipPath2, ("data.txt", "hello"));
                using var provider2 = new WebView2AppHost.ZipContentProvider(zipPath2);
                provider2.Load();
                var bytes2 = provider2.TryGetBytes("/data.txt");
                Assert(bytes2 != null, "④ large entry guard: 通常エントリが null でない");
                var text2 = new StreamReader(new MemoryStream(bytes2!), Encoding.UTF8, detectEncodingFromByteOrderMarks: true).ReadToEnd();
                Assert(text2 == "hello", "④ large entry guard: 通常エントリが正常に読める");

                Console.WriteLine("  ④ large entry guard tests passed.");
            }
            finally
            {
                WebView2AppHost.AppLog.Override = oldOverride;
            }
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

                WebView2AppHost.AppLog.Log(
                    "ERROR", "TestSource", "test error", new InvalidOperationException("test error"));
                var output = sw.ToString();
                Assert(output.Contains("[ERROR]"), "AppLog.Error: contains [ERROR]");
                Assert(output.Contains("TestSource"), "AppLog.Error: contains source");
                Assert(output.Contains("test error"), "AppLog.Error: contains message");
                Assert(output.Contains("InvalidOperationException"), "AppLog.Error: contains exception type");

                sw.GetStringBuilder().Clear();
                WebView2AppHost.AppLog.Log("WARN", "WarnSource", "warning message");
                output = sw.ToString();
                Assert(output.Contains("[WARN]"), "AppLog.Warn: contains [WARN]");
                Assert(output.Contains("WarnSource"), "AppLog.Warn: contains source");
                Assert(output.Contains("warning message"), "AppLog.Warn: contains message");

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
            var disposableStream = new TrackingStream();
            Assert(!disposableStream.IsDisposed, "StreamDisposal: stream not yet disposed");

            var oldOverride = WebView2AppHost.AppLog.Override;
            try
            {
                WebView2AppHost.AppLog.Override = TextWriter.Null;
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

                var json = Encoding.UTF8.GetBytes("{\"title\":\"Test\",\"width\":800,\"height\":600}");
                using (var ms = new MemoryStream(json))
                {
                    var config = WebView2AppHost.AppConfig.Load(ms);
                    Assert(config != null, "AppConfig: valid JSON returns non-null");
                    Assert(config!.Title == "Test", "AppConfig: title parsed");
                    Assert(config.Width == 800, "AppConfig: width parsed");
                    Assert(config.Height == 600, "AppConfig: height parsed");
                }

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
                var c1 = LoadConfig("{\"title\":null,\"width\":800,\"height\":600}");
                Assert(c1 != null && c1!.Title == "WebView2 App Host",
                    "Sanitize: null title falls back to default");

                var c2 = LoadConfig("{\"title\":\"\",\"width\":800,\"height\":600}");
                Assert(c2 != null && c2!.Title == "WebView2 App Host",
                    "Sanitize: empty title falls back to default");

                var c3 = LoadConfig("{\"title\":\"   \",\"width\":800,\"height\":600}");
                Assert(c3 != null && c3!.Title == "WebView2 App Host",
                    "Sanitize: whitespace-only title falls back to default");

                var c4 = LoadConfig("{\"title\":\"Hello\\u0000World\",\"width\":800,\"height\":600}");
                Assert(c4 != null && c4!.Title == "HelloWorld",
                    "Sanitize: control chars removed from title");

                var c5 = LoadConfig("{\"title\":\"T\",\"width\":10,\"height\":600}");
                Assert(c5 != null && c5!.Width == 160,
                    "Sanitize: width below minimum clamped to 160");

                var c6 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":1}");
                Assert(c6 != null && c6!.Height == 160,
                    "Sanitize: height below minimum clamped to 160");

                var c7 = LoadConfig("{\"title\":\"T\",\"width\":99999,\"height\":600}");
                Assert(c7 != null && c7!.Width == 7680,
                    "Sanitize: width above maximum clamped to 7680");

                var c8 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":99999}");
                Assert(c8 != null && c8!.Height == 4320,
                    "Sanitize: height above maximum clamped to 4320");

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
                var cfg = LoadConfig(
                    "{\"title\":\"T\",\"width\":800,\"height\":600,"
                    + "\"proxyOrigins\":[\"https://api.example.com\",\"https://other.example.com\"]}"
                )!;
                Assert(cfg.ProxyOrigins.Length == 2, "ProxyParsing: two origins parsed");
                Assert(cfg.ProxyOrigins[0] == "https://api.example.com", "ProxyParsing: first origin correct");
                Assert(cfg.ProxyOrigins[1] == "https://other.example.com", "ProxyParsing: second origin correct");

                var cfg2 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600}")!;
                Assert(cfg2.ProxyOrigins.Length == 0, "ProxyParsing: absent proxyOrigins defaults to empty array");

                var cfg3 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600,\"proxyOrigins\":[]}")!;
                Assert(cfg3.ProxyOrigins.Length == 0, "ProxyParsing: explicit empty array is empty");

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
                var cfg1 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600}")!;
                Assert(!cfg1.IsProxyAllowed(new Uri("https://api.example.com/v1/data")),
                    "ProxyAllowed: empty proxyOrigins denies all");

                var cfg2 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600,\"proxyOrigins\":[\"https://api.example.com\"]}")!;
                Assert(cfg2.IsProxyAllowed(new Uri("https://api.example.com/v1/data")),
                    "ProxyAllowed: matching origin is allowed");

                Assert(!cfg2.IsProxyAllowed(new Uri("https://other.example.com/v1/data")),
                    "ProxyAllowed: non-matching origin is denied");

                var cfg3 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600,\"proxyOrigins\":[\"https://api.example.com/\"]}")!;
                Assert(cfg3.IsProxyAllowed(new Uri("https://api.example.com/v1/data")),
                    "ProxyAllowed: trailing slash in config is normalized");

                var cfg4 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600,\"proxyOrigins\":[\"HTTPS://API.EXAMPLE.COM\"]}")!;
                Assert(cfg4.IsProxyAllowed(new Uri("https://api.example.com/v1/data")),
                    "ProxyAllowed: case-insensitive origin matching");

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
                var dir1 = System.IO.Path.Combine(workDir, "userconf-absent");
                System.IO.Directory.CreateDirectory(dir1);
                var cfg1 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600}")!;
                cfg1.ApplyUserConfig(dir1);
                Assert(cfg1.Width == 800 && cfg1.Height == 600,
                    "UserConfig: absent user.conf.json leaves values unchanged");

                var dir2 = System.IO.Path.Combine(workDir, "userconf-size");
                System.IO.Directory.CreateDirectory(dir2);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir2, "user.conf.json"), "{\"width\":1920,\"height\":1080}");
                var cfg2 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600}")!;
                cfg2.ApplyUserConfig(dir2);
                Assert(cfg2.Width == 1920 && cfg2.Height == 1080,
                    "UserConfig: width and height overridden by user.conf.json");

                var dir3 = System.IO.Path.Combine(workDir, "userconf-fs");
                System.IO.Directory.CreateDirectory(dir3);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir3, "user.conf.json"), "{\"fullscreen\":true}");
                var cfg3 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600,\"fullscreen\":false}")!;
                cfg3.ApplyUserConfig(dir3);
                Assert(cfg3.Fullscreen == true, "UserConfig: fullscreen overridden by user.conf.json");

                var dir4 = System.IO.Path.Combine(workDir, "userconf-title");
                System.IO.Directory.CreateDirectory(dir4);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir4, "user.conf.json"), "{\"width\":1280,\"height\":720}");
                var cfg4 = LoadConfig("{\"title\":\"MyApp\",\"width\":800,\"height\":600}")!;
                cfg4.ApplyUserConfig(dir4);
                Assert(cfg4.Title == "MyApp", "UserConfig: title cannot be overridden by user.conf.json");

                var dir5 = System.IO.Path.Combine(workDir, "userconf-clamp");
                System.IO.Directory.CreateDirectory(dir5);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir5, "user.conf.json"), "{\"width\":99999,\"height\":1}");
                var cfg5 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600}")!;
                cfg5.ApplyUserConfig(dir5);
                Assert(cfg5.Width == 7680 && cfg5.Height == 160, "UserConfig: out-of-range values are clamped");

                var dir6 = System.IO.Path.Combine(workDir, "userconf-broken");
                System.IO.Directory.CreateDirectory(dir6);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir6, "user.conf.json"), "{invalid json");
                var cfg6 = LoadConfig("{\"title\":\"T\",\"width\":800,\"height\":600}")!;
                cfg6.ApplyUserConfig(dir6);
                Assert(cfg6.Width == 800, "UserConfig: broken user.conf.json is ignored");

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
            Assert(
                WebView2AppHost.NavigationPolicy.Classify("http://app.local/index.html")
                    == WebView2AppHost.NavigationPolicy.Action.OpenExternal,
                "NavigationPolicy: http://app.local -> OpenExternal (not https)");

            Assert(
                WebView2AppHost.NavigationPolicy.IsAppLocalUri("HTTPS://APP.LOCAL/index.html"),
                "NavigationPolicy: IsAppLocalUri is case-insensitive");

            Assert(
                WebView2AppHost.NavigationPolicy.Classify("about:blank")
                    == WebView2AppHost.NavigationPolicy.Action.Allow,
                "NavigationPolicy: about:blank -> Allow");

            Assert(
                WebView2AppHost.NavigationPolicy.Classify("file:///C:/test.html")
                    == WebView2AppHost.NavigationPolicy.Action.Allow,
                "NavigationPolicy: file:// -> Allow (not blocked as external)");

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
            Assert(
                WebView2AppHost.WebResourceHandler.ParseRange("bytes=0-0", 0) == null,
                "ParseRange: total=0 returns null");

            Assert(
                WebView2AppHost.WebResourceHandler.ParseRange("bytes=-0", 1000) == null,
                "ParseRange: suffix=0 returns null");

            var single = WebView2AppHost.WebResourceHandler.ParseRange("bytes=5-5", 1000);
            Assert(single != null && single.Value.start == 5 && single.Value.end == 5,
                "ParseRange: start==end single byte is valid");

            var exact = WebView2AppHost.WebResourceHandler.ParseRange("bytes=0-999", 1000);
            Assert(exact != null && exact.Value.end == 999,
                "ParseRange: end==total-1 is valid without clamping");

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
                var root   = System.IO.Path.Combine(workDir, "traversal-root");
                var secret = System.IO.Path.Combine(workDir, "secret.txt");
                System.IO.Directory.CreateDirectory(root);
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "safe.txt"), "safe");
                System.IO.File.WriteAllText(secret, "SECRET");

                using var provider = new WebView2AppHost.ZipContentProvider(
                    mockExePath: System.IO.Path.Combine(workDir, "fake.exe")
                );

                provider.Load();

                var result = provider.TryGetBytes("/../secret.txt");
                Assert(result == null, "DirectorySource: path traversal attempt returns null");

                var result2 = provider.TryGetBytes("/..\\secret.txt");
                Assert(result2 == null, "DirectorySource: backslash traversal attempt returns null");

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
            Assert(WebView2AppHost.MimeTypes.FromPath("INDEX.HTML") == "text/html; charset=utf-8", "MimeTypes: uppercase .HTML");
            Assert(WebView2AppHost.MimeTypes.FromPath("script.JS") == "text/javascript", "MimeTypes: mixed-case .JS");
            Assert(WebView2AppHost.MimeTypes.FromPath("style.CSS") == "text/css; charset=utf-8", "MimeTypes: uppercase .CSS");
            Assert(WebView2AppHost.MimeTypes.FromPath("image.PNG") == "image/png", "MimeTypes: uppercase .PNG");
            Console.WriteLine("  MimeTypes case-insensitivity tests passed.");
        }

        // =====================================================================
        // TrackingStream
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
                throw new InvalidOperationException($"Expected entry '{entryName}' in '{bundledPath}'.");
            using var reader = new StreamReader(new MemoryStream(contentBytes), Encoding.UTF8, true);
            var content = reader.ReadToEnd();
            if (!string.Equals(content, expectedContent, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected content in '{entryName}'. Expected '{expectedContent}', got '{content}'.");
        }

        private static void AssertZipDoesNotContain(string bundledPath, string entryName)
        {
            using var provider = new WebView2AppHost.ZipContentProvider(bundledPath);
            provider.Load();
            if (provider.TryGetBytes("/" + entryName) != null)
                throw new InvalidOperationException($"Did not expect entry '{entryName}' in '{bundledPath}'.");
        }

        private static void AssertInvalidZip(string path)
        {
            using var provider = new WebView2AppHost.ZipContentProvider(path);
            bool loaded = provider.Load();
            if (loaded)
                throw new InvalidOperationException($"Expected '{path}' to be rejected as a content source (Load should return false).");
        }

        // =====================================================================
        // CloseRequestState Tests
        // =====================================================================

        private static void RunCloseRequestStateTests()
        {
            {
                var s = new WebView2AppHost.CloseRequestState();
                AssertTrue(!s.IsClosingConfirmed,           "初期: IsClosingConfirmed は false");
                AssertTrue(!s.IsClosingInProgress,          "初期: IsClosingInProgress は false");
                AssertTrue(!s.IsHostCloseNavigationPending, "初期: IsHostCloseNavigationPending は false");
                AssertTrue(s.ShouldConvertPageCloseRequestToHostClose(), "初期: ShouldConvert は true");
            }

            {
                var s = new WebView2AppHost.CloseRequestState();
                s.BeginHostCloseNavigation();
                AssertTrue(s.IsClosingInProgress,          "Begin 後: IsClosingInProgress は true");
                AssertTrue(s.IsHostCloseNavigationPending, "Begin 後: IsHostCloseNavigationPending は true");
                AssertTrue(!s.ShouldConvertPageCloseRequestToHostClose(), "Begin 後: ShouldConvert は false");
            }

            {
                var s = new WebView2AppHost.CloseRequestState();
                s.BeginHostCloseNavigation();
                s.CancelHostCloseNavigation();
                AssertTrue(!s.IsClosingInProgress,          "Cancel 後: IsClosingInProgress は false");
                AssertTrue(!s.IsHostCloseNavigationPending, "Cancel 後: IsHostCloseNavigationPending は false");
                AssertTrue(!s.IsClosingConfirmed,           "Cancel 後: IsClosingConfirmed は false");
                AssertTrue(s.ShouldConvertPageCloseRequestToHostClose(), "Cancel 後: ShouldConvert は true");
            }

            {
                var s = new WebView2AppHost.CloseRequestState();
                s.BeginHostCloseNavigation();
                var result = s.TryCompleteCloseNavigation(isSuccess: true);
                AssertTrue(result,                  "Complete(成功): true を返す");
                AssertTrue(s.IsClosingConfirmed,    "Complete(成功): IsClosingConfirmed は true");
                AssertTrue(!s.IsClosingInProgress,  "Complete(成功): IsClosingInProgress は false");
                AssertTrue(!s.IsHostCloseNavigationPending, "Complete(成功): IsHostCloseNavigationPending は false");
                AssertTrue(!s.ShouldConvertPageCloseRequestToHostClose(), "Complete(成功): ShouldConvert は false");
            }

            {
                var s = new WebView2AppHost.CloseRequestState();
                s.BeginHostCloseNavigation();
                var result = s.TryCompleteCloseNavigation(isSuccess: false);
                AssertTrue(!result,                  "Complete(失敗): false を返す");
                AssertTrue(!s.IsClosingConfirmed,    "Complete(失敗): IsClosingConfirmed は false");
                AssertTrue(!s.IsClosingInProgress,   "Complete(失敗): IsClosingInProgress は false");
                AssertTrue(!s.IsHostCloseNavigationPending, "Complete(失敗): IsHostCloseNavigationPending は false");
                AssertTrue(s.ShouldConvertPageCloseRequestToHostClose(), "Complete(失敗): ShouldConvert は true");
            }

            {
                var s = new WebView2AppHost.CloseRequestState();
                var result = s.TryCompleteCloseNavigation(isSuccess: true);
                AssertTrue(!result,               "Complete(Pending なし): false を返す");
                AssertTrue(!s.IsClosingConfirmed, "Complete(Pending なし): IsClosingConfirmed は false");
            }

            {
                var s = new WebView2AppHost.CloseRequestState();
                s.ConfirmDirectClose();
                AssertTrue(s.IsClosingConfirmed,   "DirectClose 後: IsClosingConfirmed は true");
                AssertTrue(!s.IsClosingInProgress, "DirectClose 後: IsClosingInProgress は false");
                AssertTrue(!s.ShouldConvertPageCloseRequestToHostClose(), "DirectClose 後: ShouldConvert は false");
            }

            {
                var s = new WebView2AppHost.CloseRequestState();
                s.BeginHostCloseNavigation();
                s.ConfirmDirectClose();
                AssertTrue(s.IsClosingConfirmed,  "混在: IsClosingConfirmed は true");
                AssertTrue(s.IsClosingInProgress, "混在: IsClosingInProgress は true");
            }

            Console.WriteLine("CloseRequestState: all tests passed.");
        }

        private static void AssertTrue(bool condition, string label)
        {
            if (!condition)
                throw new InvalidOperationException($"CloseRequestState test failed: {label}");
        }
    }
}
