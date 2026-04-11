using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;
using WebView2AppHost;

namespace HostTests
{
    public class ZipContentProviderTests : IDisposable
    {
        private readonly string _workDir;

        public ZipContentProviderTests()
        {
            _workDir = Path.Combine(Path.GetTempPath(), "webview2-app-host-zip-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_workDir, recursive: true); } catch { /* ignore */ }
        }

        private void CreateZip(string path, params (string Name, string Content)[] entries)
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

        private void AppendFiles(string outputPath, params string[] inputs)
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

        [Fact]
        public void Read_AppendedZip_ExtractsCorrectFile()
        {
            var prefix = Path.Combine(_workDir, "prefix.exe");
            File.WriteAllBytes(prefix, Encoding.ASCII.GetBytes("MZ-PREFIX-ONLY"));

            var zip1 = Path.Combine(_workDir, "one.zip");
            CreateZip(zip1, ("one.txt", "first"));

            var zip2 = Path.Combine(_workDir, "two.zip");
            CreateZip(zip2, ("two.txt", "second"));

            var bundled1 = Path.Combine(_workDir, "bundled1.exe");
            AppendFiles(bundled1, prefix, zip1);

            using (var provider = new ZipContentProvider(bundled1))
            {
                provider.Load();
                var bytes = provider.TryGetBytes("/one.txt");
                Assert.NotNull(bytes);
                var content = Encoding.UTF8.GetString(bytes);
                Assert.Equal("first", content);

                Assert.Null(provider.TryGetBytes("/two.txt"));
            }

            var bundled2 = Path.Combine(_workDir, "bundled2.exe");
            AppendFiles(bundled2, bundled1, zip2);

            using (var provider2 = new ZipContentProvider(bundled2))
            {
                provider2.Load();
                var bytes = provider2.TryGetBytes("/two.txt");
                Assert.NotNull(bytes);
                var content = Encoding.UTF8.GetString(bytes);
                Assert.Equal("second", content);

                Assert.Null(provider2.TryGetBytes("/one.txt"));
            }
        }

        [Fact]
        public void Load_InvalidZip_ReturnsFalse()
        {
            var prefix = Path.Combine(_workDir, "prefix.exe");
            File.WriteAllBytes(prefix, Encoding.ASCII.GetBytes("MZ-PREFIX-ONLY"));

            using (var provider = new ZipContentProvider(prefix))
            {
                Assert.False(provider.Load(), "Invalid zip should not be loaded: " + prefix);
            }
        }

        [Fact]
        public void TryGetBytes_WithDirectoryTraversalSequence_ReturnsNull()
        {
            var root = Path.Combine(_workDir, "root"); 
            Directory.CreateDirectory(root);
            var fakeZip = Path.Combine(_workDir, "fake.exe");
            File.WriteAllText(fakeZip, "PK...");

            using var p = new ZipContentProvider(fakeZip);
            Assert.Null(p.TryGetBytes("/../secret"));
        }

        [Fact]
        public void Load_WithUintOverflowSignature_HandlesCorrectly()
        {
            var tempPath = Path.Combine(_workDir, "overflow.zip");
            using (var fs = new FileStream(tempPath, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(Encoding.ASCII.GetBytes("MZ-DUMMY-PREFIX!"));
                w.Write(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
                w.Write(new byte[8]);
                w.Write((uint)0x00000200); 
                w.Write((uint)0xFFFFFF00); 
                w.Write((ushort)0);
            }

            using var provider = new ZipContentProvider(tempPath);
            // Shouldn't crash and should return false maybe since it's malformed
            var result = provider.Load(); 
            // Assert depends on current logic, wait: tests just called provider.Load() and verified no crash.
            // As per original: RunUintOverflowFixTests just loads and disposes.
        }

        [Fact]
        public void TryGetBytes_EmptyEntryInZip_ReturnsEmptyArray()
        {
            var zipPath = Path.Combine(_workDir, "empty-entry.zip");
            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create)) 
            { 
                zip.CreateEntry("empty.txt"); 
            }

            using var provider = new ZipContentProvider(zipPath);
            provider.Load();
            var bytes = provider.TryGetBytes("/empty.txt");
            Assert.NotNull(bytes);
            Assert.Empty(bytes);
        }

        [Fact]
        public void Load_StreamDisposalInvalidZip_Rejected()
        {
            var temp = Path.Combine(_workDir, "not-zip.txt");
            File.WriteAllText(temp, "not a zip");
            using var p = new ZipContentProvider(temp);
            Assert.False(p.Load(), "StreamDisposal: invalid zip rejected");
        }
    }
}
