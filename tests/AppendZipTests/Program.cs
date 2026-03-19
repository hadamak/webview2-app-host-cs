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

            // --- NavigationPolicy tests ---
            RunNavigationPolicyTests();

            // --- MimeTypes tests ---
            RunMimeTypesTests();
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

            // Invalid format
            var r6 = WebView2AppHost.WebResourceHandler.ParseRange("invalid", 1000);
            Assert(r6 == null, "ParseRange: invalid format returns null");

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

            Console.WriteLine("  SubStream tests passed.");
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
