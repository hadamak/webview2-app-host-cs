using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;

namespace WebView2AppHost
{
    /// <summary>
    /// ZIP・ディレクトリからコンテンツを提供する。
    ///
    /// コンテンツの読み込み優先順位（高い順）:
    ///   1. 連結 ZIP (EXE末尾): パッケージ化された正規の構成（最優先・保護対象）。
    ///   2. 外部指定 (.zip): コマンドライン引数。www/ より優先され、一時的なオーバーライドに便利。
    ///   3. 個別配置 (www/): メディアファイル等のルーズなアセット。
    ///   4. 同封 ZIP (.zip): EXE と同名の ZIP。
    ///   5. 埋め込み (app.zip): 最終フォールバック。
    /// </summary>
    internal sealed class ZipContentProvider : IDisposable
    {
        private readonly List<IContentSource> _sources = new List<IContentSource>();
        private readonly string? _mockExePath;
        private readonly string? _mockArgZip;

        internal ZipContentProvider(string? mockExePath = null, string? mockArgZip = null)
        {
            _mockExePath = mockExePath;
            _mockArgZip = mockArgZip;
        }

        public bool Load()
        {
            // 優先順位 1: 連結 ZIP (Security Protection Mode)
            // TryAddBundledSource 内部で Insert(0) されるため、常にリストの先頭（最優先）になる。
            bool hasBundled = TryAddBundledSource();

            // 優先順位 2: コマンドライン引数 ZIP (www/ より優先)
            TryAddArgSource();

            // 優先順位 3: www/ フォルダ
            // 連結 ZIP がある場合はセキュリティのため app.conf.json を無視する。
            var denyList = hasBundled ? new[] { "app.conf.json" } : null;
            LoadIndividualSource(denyList);

            if (!hasBundled)
            {
                // 優先順位 4: 同封 ZIP (連結 ZIP がない場合のみ)
                TryAddSiblingSource();
            }

            // 優先順位 5: 埋め込みリソース
            TryAddEmbeddedSource();

            return _sources.Count > 0;
        }

        private void LoadIndividualSource(string[]? denyList)
        {
            var exeDir = Path.GetDirectoryName(GetExePath()) ?? ".";
            var wwwDir = Path.Combine(exeDir, "www");
            if (Directory.Exists(wwwDir))
            {
                _sources.Add(new DirectorySource(wwwDir, denyList));
                AppLog.Log(AppLog.LogLevel.Info, "ZipContentProvider",
                    $"Mounted Individual Source: {wwwDir}" + (denyList != null ? " (with security filters)" : ""));
            }
        }

        private void TryAddArgSource()
        {
            if (!string.IsNullOrEmpty(_mockArgZip) && File.Exists(_mockArgZip))
            {
                var src = ZipSource.FromFile(_mockArgZip!);
                if (src != null)
                {
                    _sources.Add(src);
                    AppLog.Log(AppLog.LogLevel.Info, "ZipContentProvider", $"Mounted Mocked Arg Source: {_mockArgZip} (Overrides individual source)");
                    return;
                }
            }

            var args = Environment.GetCommandLineArgs();
            if (args.Length >= 2 && File.Exists(args[1]) && args[1].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var src = ZipSource.FromFile(args[1]);
                if (src != null)
                {
                    _sources.Add(src);
                    AppLog.Log(AppLog.LogLevel.Info, "ZipContentProvider", $"Mounted Arg Source: {args[1]} (Overrides individual source)");
                }
            }
        }

        private void TryAddSiblingSource()
        {
            var zipPath = Path.ChangeExtension(GetExePath(), ".zip");
            if (File.Exists(zipPath))
            {
                var src = ZipSource.FromFile(zipPath);
                if (src != null)
                {
                    _sources.Add(src);
                    AppLog.Log(AppLog.LogLevel.Info, "ZipContentProvider", $"Mounted Sibling Source: {zipPath}");
                }
            }
        }

        private bool TryAddBundledSource()
        {
            var exePath = GetExePath();
            try
            {
                var src = ZipSource.FromAppendedFile(exePath);
                if (src != null)
                {
                    // 連結 ZIP はリストの絶対先頭（インデックス 0）に挿入
                    _sources.Insert(0, src);
                    AppLog.Log(AppLog.LogLevel.Info, "ZipContentProvider", "Mounted Bundled Source (Highest Priority)");
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLog.Log(AppLog.LogLevel.Error, "ZipContentProvider.TryAddBundledSource", "Appended ZIP の検出に失敗", ex);
            }
            return false;
        }

        private void TryAddEmbeddedSource()
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetName().Name + ".app.zip";
            var stream = asm.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                var src = ZipSource.FromStream(stream);
                if (src != null)
                {
                    _sources.Add(src);
                    AppLog.Log(AppLog.LogLevel.Info, "ZipContentProvider", "Mounted Embedded Source");
                }
                else
                {
                    stream.Dispose();
                }
            }
        }

        public Stream? OpenEntry(string virtualPath)
        {
            foreach (var source in _sources)
            {
                var stream = source.OpenEntry(virtualPath);
                if (stream != null) return stream;
            }
            return null;
        }

        public byte[]? TryGetBytes(string virtualPath)
        {
            using (var stream = OpenEntry(virtualPath))
            {
                if (stream == null) return null;
                if (stream is MemoryStream ms) return ms.ToArray();
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            }
        }

        public void Dispose()
        {
            foreach (var source in _sources) source.Dispose();
            _sources.Clear();
        }

        private string GetExePath()
            => _mockExePath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;

        private interface IContentSource : IDisposable
        {
            Stream? OpenEntry(string virtualPath);
        }

        private sealed class DirectorySource : IContentSource
        {
            private readonly string _root;
            private readonly string[]? _denyList;

            public DirectorySource(string root, string[]? denyList = null)
            {
                _root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                _denyList = denyList;
            }

            public Stream? OpenEntry(string virtualPath)
            {
                var relative = virtualPath.TrimStart('/');
                if (string.IsNullOrEmpty(relative)) return null;

                if (_denyList != null)
                {
                    if (_denyList.Any(d => string.Equals(d, relative, StringComparison.OrdinalIgnoreCase)))
                    {
                        AppLog.Log(AppLog.LogLevel.Warn, "DirectorySource", $"Access denied to sensitive file by policy: {relative}");
                        return null;
                    }
                }

                var fullPath = Path.GetFullPath(Path.Combine(_root, relative));
                if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                if (!File.Exists(fullPath)) return null;

                return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            }
            public void Dispose() { }
        }

        private sealed class ZipSource : IContentSource
        {
            private const int MaxCachedEntryBytes = 1024 * 1024;
            private readonly ZipArchive _archive;
            private readonly Dictionary<string, byte[]> _cache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            private ZipSource(Stream stream)
            {
                _archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            }

            public static ZipSource? FromFile(string path)
            {
                FileStream? fs = null;
                try
                {
                    fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return new ZipSource(fs);
                }
                catch (Exception ex)
                {
                    fs?.Dispose();
                    AppLog.Log(AppLog.LogLevel.Error, "ZipSource.FromFile", $"ZIP エラー: {path}", ex);
                    return null;
                }
            }

            public static ZipSource? FromAppendedFile(string path)
            {
                var stream = FindAppendedZipStream(path);
                return (stream == null) ? null : FromStream(stream);
            }

            private static Stream? FindAppendedZipStream(string path)
            {
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    if (fs.Length < 22) { fs.Dispose(); return null; }
                    long readStart = Math.Max(0, fs.Length - 65557);
                    int readLen = (int)(fs.Length - readStart);
                    fs.Seek(readStart, SeekOrigin.Begin);
                    byte[] buffer = new byte[readLen];
                    fs.Read(buffer, 0, readLen);

                    int eocdIdx = -1;
                    for (int i = readLen - 22; i >= 0; i--)
                    {
                        if (buffer[i] == 0x50 && buffer[i + 1] == 0x4B && buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
                        {
                            eocdIdx = i;
                            break;
                        }
                    }

                    if (eocdIdx < 0) { fs.Dispose(); return null; }

                    uint cdSize = BitConverter.ToUInt32(buffer, eocdIdx + 12);
                    uint cdOffset = BitConverter.ToUInt32(buffer, eocdIdx + 16);
                    long totalZipSize = (long)cdOffset + cdSize + 22 + BitConverter.ToUInt16(buffer, eocdIdx + 20);

                    if (totalZipSize > fs.Length || totalZipSize <= 0) { fs.Dispose(); return null; }
                    return new OffsetStream(fs, fs.Length - totalZipSize, totalZipSize);
                }
                catch
                {
                    fs.Dispose();
                    return null;
                }
            }

            public static ZipSource? FromStream(Stream stream)
            {
                try
                {
                    return new ZipSource(stream);
                }
                catch
                {
                    stream.Dispose();
                    return null;
                }
            }

            public Stream? OpenEntry(string virtualPath)
            {
                var entryName = virtualPath.TrimStart('/');
                if (_cache.TryGetValue(entryName, out var cached))
                {
                    return new MemoryStream(cached, false);
                }

                var entry = _archive.GetEntry(entryName);
                if (entry == null || entry.Length > int.MaxValue) return null;

                using (var entryStream = entry.Open())
                {
                    var ms = new MemoryStream((int)entry.Length);
                    entryStream.CopyTo(ms);
                    ms.Position = 0;
                    if (entry.Length <= MaxCachedEntryBytes)
                    {
                        _cache[entryName] = ms.ToArray();
                    }
                    return ms;
                }
            }

            public void Dispose()
            {
                _cache.Clear();
                _archive.Dispose();
            }
        }

        private sealed class OffsetStream : Stream
        {
            private readonly Stream _base;
            private readonly long _offset;
            private readonly long _length;
            private long _position;

            public OffsetStream(Stream b, long o, long l)
            {
                _base = b;
                _offset = o;
                _length = l;
            }

            public override bool CanRead => _base.CanRead;
            public override bool CanSeek => _base.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position
            {
                get => _position;
                set => _position = Math.Max(0, Math.Min(value, _length));
            }

            public override int Read(byte[] b, int o, int c)
            {
                long rem = _length - _position;
                if (rem <= 0) return 0;
                if (c > rem) c = (int)rem;
                _base.Seek(_offset + _position, SeekOrigin.Begin);
                int r = _base.Read(b, o, c);
                _position += r;
                return r;
            }

            public override long Seek(long o, SeekOrigin or)
            {
                switch (or)
                {
                    case SeekOrigin.Begin: _position = o; break;
                    case SeekOrigin.Current: _position += o; break;
                    case SeekOrigin.End: _position = _length + o; break;
                }
                _position = Math.Max(0, Math.Min(_position, _length));
                return _position;
            }

            public override void Flush() => _base.Flush();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

            protected override void Dispose(bool d)
            {
                if (d) _base.Dispose();
                base.Dispose(d);
            }
        }
    }
}
