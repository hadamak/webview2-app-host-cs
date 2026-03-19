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
            using var fs = new FileStream(bundledPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

            var entry = zip.GetEntry(entryName);
            if (entry == null)
            {
                throw new InvalidOperationException($"Expected entry '{entryName}' in '{bundledPath}'.");
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();
            if (!string.Equals(content, expectedContent, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected content in '{entryName}'. Expected '{expectedContent}', got '{content}'.");
            }
        }

        private static void AssertZipDoesNotContain(string bundledPath, string entryName)
        {
            using var fs = new FileStream(bundledPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

            if (zip.GetEntry(entryName) != null)
            {
                throw new InvalidOperationException($"Did not expect entry '{entryName}' in '{bundledPath}'.");
            }
        }

        private static void AssertInvalidZip(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                using var _ = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            }
            catch (InvalidDataException)
            {
                return;
            }

            throw new InvalidOperationException($"Expected '{path}' to be rejected as a ZIP archive.");
        }
    }
}
