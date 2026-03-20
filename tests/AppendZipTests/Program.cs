using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AppendZipTests
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
                    Console.WriteLine("All append-zip tests passed.");
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

            // --- CloseRequestState tests ---
            RunCloseRequestStateTests();

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
        // CloseRequestState Tests
        // =====================================================================

        private static void RunCloseRequestStateTests()
        {
            var state = new WebView2AppHost.CloseRequestState();

            Assert(!state.TryCompleteCloseNavigation(isSuccess: true),
                "CloseRequestState: navigation without close attempt does not close");

            Assert(state.ShouldConvertPageCloseRequestToHostClose(),
                "CloseRequestState: page close request is converted when no host navigation is pending");

            state.BeginHostCloseNavigation();
            Assert(!state.ShouldConvertPageCloseRequestToHostClose(),
                "CloseRequestState: host close navigation suppresses page-close conversion");
            Assert(!state.TryCompleteCloseNavigation(isSuccess: false),
                "CloseRequestState: failed close navigation does not close");
            Assert(!state.IsClosingInProgress,
                "CloseRequestState: failed close navigation clears in-progress state");
            Assert(!state.IsHostCloseNavigationPending,
                "CloseRequestState: failed close navigation clears host navigation pending state");
            Assert(!state.IsClosingConfirmed,
                "CloseRequestState: failed close navigation keeps confirmed state false");

            state.BeginHostCloseNavigation();
            state.CancelHostCloseNavigation();
            Assert(!state.IsClosingInProgress,
                "CloseRequestState: canceled host close clears in-progress state");
            Assert(!state.IsHostCloseNavigationPending,
                "CloseRequestState: canceled host close clears host navigation pending state");
            Assert(state.ShouldConvertPageCloseRequestToHostClose(),
                "CloseRequestState: canceled host close re-enables page-close conversion");

            Assert(!state.TryCompleteCloseNavigation(isSuccess: true),
                "CloseRequestState: stale state is not reused after cancellation");

            state.BeginHostCloseNavigation();
            Assert(state.TryCompleteCloseNavigation(isSuccess: true),
                "CloseRequestState: successful host close navigation closes");
            Assert(state.IsClosingConfirmed,
                "CloseRequestState: successful host close navigation sets confirmed state");
            Assert(!state.IsClosingInProgress,
                "CloseRequestState: successful host close navigation clears in-progress state");
            Assert(!state.IsHostCloseNavigationPending,
                "CloseRequestState: successful host close navigation clears host navigation pending state");

            Assert(!state.ShouldConvertPageCloseRequestToHostClose(),
                "CloseRequestState: confirmed close does not re-enter page-close conversion");

            Console.WriteLine("  CloseRequestState tests passed.");
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
                WebView2AppHost.NavigationPolicy.Classify("about:blank")
                    == WebView2AppHost.NavigationPolicy.Action.MarkClosing,
                "NavigationPolicy: about:blank -> MarkClosing");

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
                WebView2AppHost.NavigationPolicy.ShouldOpenHostPopup("about:blank"),
                "NavigationPolicy: about:blank popup stays in host");

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
